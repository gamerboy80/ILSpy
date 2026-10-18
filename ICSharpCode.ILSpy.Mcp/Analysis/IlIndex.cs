// Copyright (c) 2026 gamerboy80
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;

using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.ILSpy.Mcp.Analysis
{
	internal sealed class IlIndex
	{
		public IReadOnlyDictionary<string, IReadOnlyList<InstructionInfo>> Instructions { get; }
		public IReadOnlyDictionary<string, IndexedMethod> Methods { get; }
		public IReadOnlyList<ReferenceInfo> References { get; }
		public IReadOnlyList<CallEdge> CallEdges { get; }
		public IReadOnlyDictionary<string, IReadOnlyList<CallEdge>> CallsBySource { get; }
		public IReadOnlyDictionary<string, IReadOnlyList<CallEdge>> CallsByTarget { get; }
		public IReadOnlyList<UnresolvedCall> UnresolvedCalls { get; }
		public IReadOnlyList<InstructionInfo> AllReferences { get; }

		IlIndex(Dictionary<string, IReadOnlyList<InstructionInfo>> instructions,
			Dictionary<string, IndexedMethod> methods, List<ReferenceInfo> references,
			List<CallEdge> callEdges, List<UnresolvedCall> unresolvedCalls, List<InstructionInfo> allReferences)
		{
			Instructions = instructions;
			Methods = methods;
			References = references;
			CallEdges = callEdges;
			CallsBySource = callEdges.GroupBy(edge => edge.SourceSymbolId, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => (IReadOnlyList<CallEdge>)group.ToArray(), StringComparer.Ordinal);
			CallsByTarget = callEdges.GroupBy(edge => edge.TargetSymbolId, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => (IReadOnlyList<CallEdge>)group.ToArray(), StringComparer.Ordinal);
			UnresolvedCalls = unresolvedCalls;
			AllReferences = allReferences;
		}

		public static IlIndex Build(AnalysisWorkspace workspace, CancellationToken cancellationToken)
		{
			var instructionsByMethod = new Dictionary<string, IReadOnlyList<InstructionInfo>>(StringComparer.Ordinal);
			var methods = new Dictionary<string, IndexedMethod>(StringComparer.Ordinal);
			var references = new List<ReferenceInfo>();
			var callEdges = new List<CallEdge>();
			var unresolvedCalls = new List<UnresolvedCall>();
			var allReferences = new List<InstructionInfo>();
			foreach (var module in workspace.LoadedModules)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var typeSystem = workspace.CreateDecompiler(module, cancellationToken).TypeSystem;
				foreach (var type in typeSystem.MainModule.TypeDefinitions)
				{
					cancellationToken.ThrowIfCancellationRequested();
					foreach (var method in type.Methods)
					{
						if (method.MetadataToken.Kind != HandleKind.MethodDefinition || !method.HasBody)
							continue;
						var handle = (MethodDefinitionHandle)method.MetadataToken;
						string sourceId = workspace.GetSymbolId(module.File, handle);
						if (sourceId.Length == 0)
							continue;
						MethodBodyBlock body;
						try
						{
							var definition = module.File.Metadata.GetMethodDefinition(handle);
							body = module.File.GetMethodBody(definition.RelativeVirtualAddress);
						}
						catch (BadImageFormatException ex)
						{
							module.Diagnostics.Add($"Could not read IL for {method.FullName}: {ex.Message}");
							continue;
						}
						var decoded = DecodeMethod(workspace, module, typeSystem.MainModule, method, handle, body, cancellationToken);
						instructionsByMethod[sourceId] = decoded.Instructions;
						methods[sourceId] = new IndexedMethod(sourceId, method.FullName, handle, body,
							BuildGraph(sourceId, module, method, handle, body, decoded.Instructions));
						foreach (var instruction in decoded.Instructions)
						{
							if (instruction.AccessKind == "unresolved indirect call")
								unresolvedCalls.Add(new UnresolvedCall(sourceId, method.FullName, instruction.Offset, instruction.OpCode, instruction.Operand));
							if (instruction.TargetSymbolId != null)
							{
								var reference = new ReferenceInfo(sourceId, method.FullName, instruction.TargetSymbolId,
									instruction.TargetName ?? instruction.TargetSymbolId, instruction.AccessKind ?? "use",
									instruction.Offset, instruction.OpCode);
								references.Add(reference);
								if (instruction.AccessKind is "call" or "virtual call" or "constructor")
								{
									callEdges.Add(new CallEdge(sourceId, method.FullName, instruction.TargetSymbolId,
										instruction.TargetName ?? instruction.TargetSymbolId,
										instruction.AccessKind == "virtual call" ? "virtual dispatch candidate" : instruction.AccessKind,
										instruction.Offset));
								}
							}
							if (instruction.TargetName != null || instruction.Literal != null)
								allReferences.Add(instruction with { SourceSymbolId = sourceId, SourceName = method.FullName });
						}
					}
				}
			}
			return new IlIndex(instructionsByMethod, methods, references, callEdges, unresolvedCalls, allReferences);
		}

		static DecodedMethod DecodeMethod(AnalysisWorkspace workspace, AnalysisWorkspace.LoadedModule module,
			MetadataModule typeSystemModule, IMethod method, MethodDefinitionHandle handle, MethodBodyBlock body,
			CancellationToken cancellationToken)
		{
			var blob = body.GetILReader();
			var result = new List<InstructionInfo>();
			var metadata = module.File.Metadata;
			while (blob.RemainingBytes > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int offset = blob.Offset;
				try
				{
					ILOpCode opCode = blob.DecodeOpCode();
					ICSharpCode.Decompiler.Metadata.OperandType operandType = opCode.GetOperandType();
					string operand = string.Empty;
					string? targetId = null;
					string? targetName = null;
					string? literal = null;
					string? accessKind = null;
					var branchTargets = new List<int>();
					int operandStart = blob.Offset;
					switch (operandType)
					{
						case ICSharpCode.Decompiler.Metadata.OperandType.ShortBrTarget:
							branchTargets.Add(blob.Offset + 1 + blob.ReadSByte());
							operand = "IL_" + branchTargets[0].ToString("X4");
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.BrTarget:
							branchTargets.Add(blob.Offset + 4 + blob.ReadInt32());
							operand = "IL_" + branchTargets[0].ToString("X4");
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.Switch:
							uint branchCount = blob.ReadUInt32();
							if (branchCount > 10_000 || branchCount * 4 > blob.RemainingBytes)
								throw new BadImageFormatException("Invalid switch branch table.");
							var deltas = new int[(int)branchCount];
							for (int i = 0; i < deltas.Length; i++)
								deltas[i] = blob.ReadInt32();
							int switchBase = blob.Offset;
							branchTargets.AddRange(deltas.Select(delta => switchBase + delta));
							operand = string.Join(", ", branchTargets.Select(target => "IL_" + target.ToString("X4")));
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.String:
							int userStringToken = blob.ReadInt32();
							try
							{
								literal = metadata.GetUserString(MetadataTokens.UserStringHandle(userStringToken & 0x00FFFFFF));
								operand = "\"" + literal.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
							}
							catch (BadImageFormatException)
							{
								operand = "0x" + userStringToken.ToString("X8");
							}
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.ShortI:
							literal = blob.ReadSByte().ToString(CultureInfo.InvariantCulture);
							operand = literal;
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.I:
							literal = blob.ReadInt32().ToString(CultureInfo.InvariantCulture);
							operand = literal;
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.I8:
							literal = blob.ReadInt64().ToString(CultureInfo.InvariantCulture);
							operand = literal;
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.ShortR:
							literal = blob.ReadSingle().ToString("R", CultureInfo.InvariantCulture);
							operand = literal;
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.R:
							literal = blob.ReadDouble().ToString("R", CultureInfo.InvariantCulture);
							operand = literal;
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.Variable:
							operand = FormatVariable(opCode, blob.ReadUInt16());
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.ShortVariable:
							operand = FormatVariable(opCode, blob.ReadByte());
							break;
						case ICSharpCode.Decompiler.Metadata.OperandType.Field:
						case ICSharpCode.Decompiler.Metadata.OperandType.Method:
						case ICSharpCode.Decompiler.Metadata.OperandType.Sig:
						case ICSharpCode.Decompiler.Metadata.OperandType.Tok:
						case ICSharpCode.Decompiler.Metadata.OperandType.Type:
							int token = blob.ReadInt32();
							try
							{
								var entityHandle = MetadataTokens.EntityHandle(token);
								var entity = typeSystemModule.ResolveEntity(entityHandle);
								targetName = entity?.FullName;
								targetId = entity == null ? null : workspace.GetSymbolId(entity.ParentModule?.MetadataFile, entity.MetadataToken);
								if (entity is IMethod targetMethod)
									accessKind = opCode switch {
										ILOpCode.Callvirt => targetMethod.IsVirtual || targetMethod.IsAbstract
											|| targetMethod.DeclaringTypeDefinition?.Kind == TypeKind.Interface ? "virtual call" : "call",
										ILOpCode.Newobj => "constructor",
										ILOpCode.Ldftn or ILOpCode.Ldvirtftn => "function pointer",
										ILOpCode.Call => "call",
										_ => "method reference"
									};
								else if (entity is IField)
									accessKind = opCode switch {
										ILOpCode.Stfld or ILOpCode.Stsfld => "field write",
										ILOpCode.Ldflda or ILOpCode.Ldsflda => "field address",
										_ => "field read"
									};
								else if (entity is ITypeDefinition)
									accessKind = "type reference";
								operand = targetName ?? "0x" + token.ToString("X8");
							}
							catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is ArgumentException)
							{
								operand = "0x" + token.ToString("X8");
							}
							break;
						default:
							ILParser.SkipOperand(ref blob, opCode);
							int byteCount = blob.Offset - operandStart;
							if (byteCount > 0)
								operand = $"[{byteCount} operand bytes]";
							break;
					}
					if (literal == null && TryGetImplicitInteger(opCode, out int implicitInteger))
					{
						literal = implicitInteger.ToString(CultureInfo.InvariantCulture);
						operand = literal;
					}
					if (opCode == ILOpCode.Calli)
					{
						accessKind = "unresolved indirect call";
						targetId = null;
						targetName = "indirect calli";
					}
					result.Add(new InstructionInfo(offset, opCode.ToString().Replace('_', '.').ToLowerInvariant(), operand,
						blob.Offset - offset, targetId, targetName, literal, accessKind, branchTargets));
				}
				catch (BadImageFormatException ex)
				{
					module.Diagnostics.Add($"Could not decode IL at {method.FullName}+IL_{offset:X4}: {ex.Message}");
					break;
				}
			}
			return new DecodedMethod(result);
		}

		static string FormatVariable(ILOpCode opCode, int index)
		{
			string name = opCode.ToString();
			char prefix = name.StartsWith("Ldarg", StringComparison.Ordinal)
				|| name.StartsWith("Starg", StringComparison.Ordinal) ? 'A' : 'V';
			return prefix + "_" + index.ToString(CultureInfo.InvariantCulture);
		}

		static bool TryGetImplicitInteger(ILOpCode opCode, out int value)
		{
			value = opCode switch {
				ILOpCode.Ldc_i4_m1 => -1,
				ILOpCode.Ldc_i4_0 => 0,
				ILOpCode.Ldc_i4_1 => 1,
				ILOpCode.Ldc_i4_2 => 2,
				ILOpCode.Ldc_i4_3 => 3,
				ILOpCode.Ldc_i4_4 => 4,
				ILOpCode.Ldc_i4_5 => 5,
				ILOpCode.Ldc_i4_6 => 6,
				ILOpCode.Ldc_i4_7 => 7,
				ILOpCode.Ldc_i4_8 => 8,
				_ => 0
			};
			return opCode is ILOpCode.Ldc_i4_m1 or ILOpCode.Ldc_i4_0 or ILOpCode.Ldc_i4_1 or ILOpCode.Ldc_i4_2
				or ILOpCode.Ldc_i4_3 or ILOpCode.Ldc_i4_4 or ILOpCode.Ldc_i4_5 or ILOpCode.Ldc_i4_6
				or ILOpCode.Ldc_i4_7 or ILOpCode.Ldc_i4_8;
		}

		static ControlFlowGraph BuildGraph(string sourceId, AnalysisWorkspace.LoadedModule module, IMethod method,
			MethodDefinitionHandle handle, MethodBodyBlock body, IReadOnlyList<InstructionInfo> instructions)
		{
			if (instructions.Count == 0)
				return new ControlFlowGraph(sourceId, body.GetILReader().Length, body.MaxStack,
					Array.Empty<BasicBlock>(), Array.Empty<ExceptionRegionInfo>(), Array.Empty<string>());
			var boundaries = new SortedSet<int> { 0, body.GetILReader().Length };
			foreach (var region in body.ExceptionRegions)
			{
				boundaries.Add(region.TryOffset);
				boundaries.Add(region.TryOffset + region.TryLength);
				boundaries.Add(region.HandlerOffset);
				boundaries.Add(region.HandlerOffset + region.HandlerLength);
				if (region.Kind == ExceptionRegionKind.Filter)
					boundaries.Add(region.FilterOffset);
			}
			foreach (var instruction in instructions)
			{
				foreach (int target in instruction.BranchTargets)
					if (target >= 0 && target <= body.GetILReader().Length)
						boundaries.Add(target);
				if (instruction.BranchTargets.Count > 0 || IsTerminator(instruction.OpCode))
					if (instruction.Offset + instruction.Length < body.GetILReader().Length)
						boundaries.Add(instruction.Offset + instruction.Length);
			}
			var starts = boundaries.ToArray();
			var blockStarts = starts.Take(starts.Length - 1).ToArray();
			var blocks = new List<BasicBlock>();
			for (int i = 0; i < blockStarts.Length; i++)
			{
				int start = starts[i];
				int end = starts[i + 1];
				var blockInstructions = instructions.Where(ins => ins.Offset >= start && ins.Offset < end).ToArray();
				if (blockInstructions.Length == 0)
					continue;
				var last = blockInstructions[^1];
				var successors = new SortedSet<int>();
				foreach (int target in last.BranchTargets)
					if (target >= 0 && target < body.GetILReader().Length)
						successors.Add(target);
				if (i + 1 < blockStarts.Length && !IsUnconditionalBranch(last.OpCode) && !IsTerminator(last.OpCode))
					successors.Add(starts[i + 1]);
				blocks.Add(new BasicBlock(start, end, blockInstructions.Select(ins => ins.Offset).ToArray(), successors.ToArray()));
			}
			var regions = body.ExceptionRegions.Select(region => new ExceptionRegionInfo(region.Kind.ToString(), region.TryOffset,
				region.TryLength, region.HandlerOffset, region.HandlerLength, region.FilterOffset,
				region.Kind == ExceptionRegionKind.Catch ? ResolveCatchType(module, region.CatchType) : null)).ToArray();
			return new ControlFlowGraph(sourceId, body.GetILReader().Length, body.MaxStack,
				blocks, regions, GetLocalNames(module, body));
		}

		static IReadOnlyList<string> GetLocalNames(AnalysisWorkspace.LoadedModule module, MethodBodyBlock body)
		{
			if (body.LocalSignature.IsNil)
				return Array.Empty<string>();
			try
			{
				var reader = module.File.Metadata.GetBlobReader(module.File.Metadata.GetStandaloneSignature(body.LocalSignature).Signature);
				reader.ReadSignatureHeader();
				int count = reader.ReadCompressedInteger();
				if (count < 0 || count > 65_536)
					return Array.Empty<string>();
				return Enumerable.Range(0, count).Select(i => "V_" + i).ToArray();
			}
			catch (BadImageFormatException)
			{
				return Array.Empty<string>();
			}
		}

		static string? ResolveCatchType(AnalysisWorkspace.LoadedModule module, EntityHandle handle)
		{
			try
			{
				var metadata = module.File.Metadata;
				return handle.Kind switch {
					HandleKind.TypeDefinition => GetTypeDefinitionName(metadata, (TypeDefinitionHandle)handle),
					HandleKind.TypeReference => GetTypeReferenceName(metadata, (TypeReferenceHandle)handle),
					_ => "0x" + MetadataTokens.GetToken(handle).ToString("X8")
				};
			}
			catch (Exception ex) when (ex is BadImageFormatException || ex is ArgumentException || ex is InvalidCastException)
			{
				return "0x" + MetadataTokens.GetToken(handle).ToString("X8");
			}
		}

		static string GetTypeDefinitionName(MetadataReader metadata, TypeDefinitionHandle handle)
		{
			var type = metadata.GetTypeDefinition(handle);
			string name = metadata.GetString(type.Name);
			string ns = metadata.GetString(type.Namespace);
			return ns.Length == 0 ? name : ns + "." + name;
		}

		static string GetTypeReferenceName(MetadataReader metadata, TypeReferenceHandle handle)
		{
			var type = metadata.GetTypeReference(handle);
			string name = metadata.GetString(type.Name);
			string ns = metadata.GetString(type.Namespace);
			return ns.Length == 0 ? name : ns + "." + name;
		}

		static bool IsTerminator(string opCode) => opCode.StartsWith("ret", StringComparison.Ordinal)
			|| opCode.StartsWith("throw", StringComparison.Ordinal) || opCode.StartsWith("rethrow", StringComparison.Ordinal)
			|| opCode.StartsWith("jmp", StringComparison.Ordinal) || opCode.StartsWith("endfinally", StringComparison.Ordinal)
			|| opCode.StartsWith("endfilter", StringComparison.Ordinal);

		static bool IsUnconditionalBranch(string opCode) => opCode is "br" or "br.s" or "leave" or "leave.s" or "jmp";

		sealed record DecodedMethod(IReadOnlyList<InstructionInfo> Instructions);
	}

	internal sealed record IndexedMethod(string SymbolId, string Name, MethodDefinitionHandle Handle,
		MethodBodyBlock Body, ControlFlowGraph ControlFlowGraph);
}
