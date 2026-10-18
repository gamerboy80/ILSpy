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

using System.Collections.Generic;

namespace ICSharpCode.ILSpy.Mcp.Analysis
{
	public sealed record AnalysisLimits(int MaximumPageSize = 200, int MaximumSourceCharacters = 16_000,
		int MaximumGraphDepth = 10, int MaximumGraphNodes = 2_000);

	public sealed record SymbolMatch(string SymbolId, string Name, string Kind, string FullName,
		string Signature, string DocumentationId, int MetadataToken, string AssemblyId, string AssemblyName);

	public sealed record SymbolDetails(SymbolMatch Symbol, string? DeclaringType,
		IReadOnlyList<string> Attributes, IReadOnlyList<string> BaseTypes, IReadOnlyList<string> Interfaces);

	public sealed record DecompiledCode(string SymbolId, string Code, string AssemblyHash, bool IsTruncated, string? NextCursor,
		IReadOnlyList<string> Diagnostics);

	public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, bool IsComplete, int TotalMatches);

	public sealed record ResourceInfo(string Name, string Kind, long? Length, bool IsPublic);

	public sealed record AssemblyOverview(string AssemblyId, string Path, string Name, string FullName,
		string TargetFramework, string Architecture, string ContentHash, string ModuleVersionId,
		string? EntryPoint, IReadOnlyList<string> References, IReadOnlyList<ResourceInfo> Resources,
		IReadOnlyList<string> Diagnostics);

	public sealed record PackageEntryInfo(string Name, string FullName, long? Length, bool IsManagedAssembly);

	public sealed record PackageInfo(string Path, string Kind, IReadOnlyList<PackageEntryInfo> Entries);

	public sealed record InstructionInfo(int Offset, string OpCode, string Operand, int Length,
		string? TargetSymbolId, string? TargetName, string? Literal, string? AccessKind,
		IReadOnlyList<int> BranchTargets, string SourceSymbolId = "", string SourceName = "");

	public sealed record ReferenceInfo(string SourceSymbolId, string SourceName, string TargetSymbolId,
		string TargetName, string Kind, int Offset, string OpCode);

	public sealed record CallEdge(string SourceSymbolId, string SourceName, string TargetSymbolId,
		string TargetName, string Dispatch, int Offset);

	public sealed record UnresolvedCall(string SourceSymbolId, string SourceName, int Offset, string OpCode, string Operand);

	public sealed record GraphNode(string SymbolId, string Name, int Depth);

	public sealed record CallGraphResult(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<CallEdge> Edges,
		bool IsTruncated, int OmittedNodeCount, int OmittedEdgeCount, IReadOnlyList<UnresolvedCall> UnresolvedCalls);

	public sealed record BasicBlock(int StartOffset, int EndOffset, IReadOnlyList<int> InstructionOffsets,
		IReadOnlyList<int> Successors);

	public sealed record ExceptionRegionInfo(string Kind, int TryOffset, int TryLength, int HandlerOffset,
		int HandlerLength, int FilterOffset, string? CatchType);

	public sealed record ControlFlowGraph(string SymbolId, int CodeSize, int MaxStack,
		IReadOnlyList<BasicBlock> Blocks, IReadOnlyList<ExceptionRegionInfo> ExceptionRegions,
		IReadOnlyList<string> Locals, bool IsTruncated = false, int OmittedBlockCount = 0);

	public sealed record ResourceData(string Name, string MediaType, string? TextPreview, string? Base64Preview,
		long? Length, bool IsTruncated);

	public sealed record FeatureFinding(string Feature, string Evidence, string SymbolId, string SymbolName,
		int Offset, string Confidence);

	public sealed record SearchEvidence(string Kind, string Value, string SymbolId, string SymbolName,
		string AssemblyId, int Offset, string? OpCode);

	public sealed record TypeRelationshipResult(string SymbolId, string TypeName, IReadOnlyList<string> BaseTypes,
		IReadOnlyList<string> Interfaces, IReadOnlyList<string> DerivedTypes, IReadOnlyList<string> Overrides);

	public sealed record SymbolContext(SymbolDetails Details, DecompiledCode Source,
		IReadOnlyList<ReferenceInfo> References, CallGraphResult CallGraph,
		IReadOnlyList<string> RelevantLiterals);

	public sealed record SymbolChange(string SymbolId, string Name, string ChangeKind, string? Before,
		string? After);

	public sealed record AssemblyComparison(string BeforeAssemblyId, string AfterAssemblyId,
		IReadOnlyList<SymbolChange> Changes, bool IsTruncated, int TotalChanges);

	public sealed record ExportResult(string Path, int FilesWritten, long BytesWritten);

	public sealed record WorkspaceAssemblyList(IReadOnlyList<AssemblyOverview> Assemblies, IReadOnlyList<PackageInfo> Packages);

	public sealed record SearchResult(Page<SearchEvidence> Results, IReadOnlyList<string> SearchedAssemblies,
		IReadOnlyList<string> Diagnostics);

	public sealed record ReferenceSearchResult(Page<ReferenceInfo> Results, IReadOnlyList<string> SearchedAssemblies);

	public sealed record InstructionSearchResult(Page<InstructionInfo> Results);

	public sealed record CallPathResult(bool Found, IReadOnlyList<CallEdge> Path, bool IsComplete);

	public sealed record FeatureSearchResult(Page<FeatureFinding> Results, string Confidence);
}
