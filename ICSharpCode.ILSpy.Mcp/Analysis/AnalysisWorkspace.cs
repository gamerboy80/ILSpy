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
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.FileLoaders;

namespace ICSharpCode.ILSpy.Mcp.Analysis
{
	public sealed class AnalysisWorkspace : IDisposable
	{
		const int DefaultPageSize = 50;
		const int MaximumSourceLength = 16_000;
		const int MaximumResourcePreview = 32_768;
		const long MaximumResourceContainerSize = 16 * 1024 * 1024;
		const int MaximumDirectoryAssemblies = 20_000;
		readonly int maximumPageSize;
		readonly int maximumSourceCharacters;
		readonly int maximumGraphDepth;
		readonly int maximumGraphNodes;
		readonly Dictionary<string, LoadedModule> modules = new(StringComparer.Ordinal);
		readonly Dictionary<string, List<LoadedModule>> modulesByAssemblyName = new(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, LoadedPackage> packages = new(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, PackageDependency> packageDependencies = new(StringComparer.Ordinal);
		readonly List<string> inputDiagnostics = new();
		readonly List<string> referenceDirectories;
		readonly FileLoaderRegistry loaderRegistry = new();
		readonly object indexLock = new();
		readonly object packageDependencyLock = new();
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213", Justification = "Operations waiting on this lock must be able to observe that the workspace is disposed.")]
		readonly ReaderWriterLockSlim lifecycleLock = new(LockRecursionPolicy.SupportsRecursion);
		IReadOnlyList<IndexedSymbol>? symbols;
		IlIndex? ilIndex;
		int revision;
		bool disposed;

		public AnalysisWorkspace(IEnumerable<string> assemblyPaths, IEnumerable<string>? additionalReferenceDirectories = null,
			CancellationToken cancellationToken = default, AnalysisLimits? limits = null)
		{
			ArgumentNullException.ThrowIfNull(assemblyPaths);
			limits ??= new AnalysisLimits();
			maximumPageSize = Math.Clamp(limits.MaximumPageSize, 1, 10_000);
			maximumSourceCharacters = Math.Clamp(limits.MaximumSourceCharacters, 8, 1_000_000);
			maximumGraphDepth = Math.Clamp(limits.MaximumGraphDepth, 0, 100);
			maximumGraphNodes = Math.Clamp(limits.MaximumGraphNodes, 1, 10_000);
			referenceDirectories = new List<string>();
			if (additionalReferenceDirectories != null)
			{
				foreach (string path in additionalReferenceDirectories)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (Directory.Exists(path))
						referenceDirectories.Add(Path.GetFullPath(path));
				}
			}
			foreach (string path in assemblyPaths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (Directory.Exists(path))
				{
					foreach (string candidate in EnumerateAssemblies(path, cancellationToken))
						TryLoadInput(candidate, cancellationToken);
				}
				else if (File.Exists(path))
				{
					TryLoadInput(path, cancellationToken);
				}
			}
		}

		public IReadOnlyList<AssemblyOverview> Assemblies {
			get => GetAssemblies();
		}

		public IReadOnlyList<string> InputDiagnostics {
			get {
				using var operation = EnterRead();
				return inputDiagnostics.ToArray();
			}
		}

		public IReadOnlyList<AssemblyOverview> GetAssemblies(CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var results = new List<AssemblyOverview>();
			foreach (var module in modules.Values)
			{
				cancellationToken.ThrowIfCancellationRequested();
				results.Add(CreateOverview(module, cancellationToken));
			}
			return results.OrderBy(assembly => assembly.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		}

		public AssemblyOverview LoadAssemblyPath(string path, CancellationToken cancellationToken = default)
		{
			using var operation = EnterWrite();
			cancellationToken.ThrowIfCancellationRequested();
			string fullPath = Path.GetFullPath(path);
			TryLoadInput(fullPath, cancellationToken);
			var loaded = modules.Values.FirstOrDefault(m => Path.GetFullPath(m.Path).Equals(fullPath, PathComparison));
			if (loaded != null)
				return CreateOverview(loaded, cancellationToken);
			if (packages.ContainsKey(fullPath))
				throw new InvalidOperationException("The input is a package. List its entries, then load a managed assembly entry.");
			throw new BadImageFormatException($"'{path}' is not a supported managed assembly or archive. Native and AOT images are not supported by this static metadata server.");
		}

		public IReadOnlyList<PackageInfo> Packages {
			get => GetPackages();
		}

		public IReadOnlyList<PackageInfo> GetPackages(CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var results = new List<PackageInfo>();
			foreach (var package in packages)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var entries = new List<PackageEntryInfo>();
				foreach (var entry in package.Value.Entries)
				{
					cancellationToken.ThrowIfCancellationRequested();
					entries.Add(new PackageEntryInfo(entry.Name, entry.FullName, entry.TryGetLength(), LooksLikeManagedAssembly(entry.Name)));
				}
				results.Add(new PackageInfo(package.Key, package.Value.Kind.ToString(), entries));
			}
			return results;
		}

		public IReadOnlyList<SymbolMatch> SearchSymbols(string query, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(query))
				return Array.Empty<SymbolMatch>();
			return GetSymbols(cancellationToken).Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| s.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| s.DocumentationId.Contains(query, StringComparison.OrdinalIgnoreCase))
				.Select(s => s.Match).Take(maximumPageSize).ToArray();
		}

		public Page<SymbolMatch> Search(string query, string? assemblyId = null, string? cursor = null, int limit = DefaultPageSize,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			limit = Math.Clamp(limit, 1, maximumPageSize);
			string cursorContext = "symbols|" + query + "|" + assemblyId;
			int start = ReadCursor(cursor, cursorContext);
			var matches = GetSymbols(cancellationToken).Where(s => (assemblyId == null || s.Match.AssemblyId == assemblyId)
				&& (string.IsNullOrWhiteSpace(query) || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
					|| s.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)
					|| s.DocumentationId.Contains(query, StringComparison.OrdinalIgnoreCase)))
				.Select(s => s.Match).ToArray();
			start = Math.Min(start, matches.Length);
			var page = matches.Skip(start).Take(limit).ToArray();
			int next = start + page.Length;
			return new Page<SymbolMatch>(page, next < matches.Length ? WriteCursor(next, cursorContext) : null, next >= matches.Length, matches.Length);
		}

		public SymbolMatch ResolveSymbol(string identifier, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(identifier))
				throw new ArgumentException("A symbol ID, documentation ID, or full name is required.", nameof(identifier));
			var all = GetSymbols(cancellationToken);
			var exact = all.Where(s => s.Match.SymbolId.Equals(identifier, StringComparison.Ordinal)
				|| s.DocumentationId.Equals(identifier, StringComparison.Ordinal)
				|| s.FullName.Equals(identifier, StringComparison.Ordinal)).ToArray();
			if (exact.Length == 1)
				return exact[0].Match;
			if (exact.Length > 1)
				throw new InvalidOperationException("The symbol identifier is ambiguous. Use a symbol ID or documentation ID.");
			var byName = all.Where(s => s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase)
				|| s.FullName.EndsWith("." + identifier, StringComparison.OrdinalIgnoreCase)).Take(3).ToArray();
			if (byName.Length == 1)
				return byName[0].Match;
			if (byName.Length > 1)
				throw new InvalidOperationException("The symbol name is ambiguous. Use a full name, documentation ID, or symbol ID.");
			throw new KeyNotFoundException($"No symbol matches '{identifier}'. Search symbols to get a stable symbol ID.");
		}

		public DecompiledCode Decompile(string identifier, string? cursor = null, int maxCharacters = MaximumSourceLength,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			var module = GetModule(symbol.Match.AssemblyId);
			var decompiler = CreateDecompiler(module, cancellationToken);
			string source = decompiler.DecompileAsString(symbol.Handle);
			int offset = ReadSourceCursor(cursor, symbol.Match.SymbolId);
			int start = Math.Min(offset, source.Length);
			int length = Math.Clamp(maxCharacters, 8, maximumSourceCharacters);
			var page = FormatSourcePage(source, start, length);
			int next = start + page.SourceCharacters;
			return new DecompiledCode(symbol.Match.SymbolId, page.Code, module.ContentHash,
				next < source.Length, next < source.Length ? WriteSourceCursor(symbol.Match.SymbolId, next) : null,
				CreateOverview(module, cancellationToken).Diagnostics);
		}

		public AssemblyOverview GetOverview(string assemblyId, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			cancellationToken.ThrowIfCancellationRequested();
			return CreateOverview(GetModule(assemblyId), cancellationToken);
		}

		public SymbolDetails InspectSymbol(string identifier, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			var type = symbol.Entity as ITypeDefinition;
			var bases = type?.DirectBaseTypes.Select(t => t.FullName).ToArray() ?? Array.Empty<string>();
			return new SymbolDetails(symbol.Match, symbol.Entity.DeclaringTypeDefinition?.FullName,
				symbol.Entity.GetAttributes().Select(a => a.AttributeType.FullName).ToArray(), bases,
				 type?.DirectBaseTypes.Where(t => t.Kind == TypeKind.Interface).Select(t => t.FullName).ToArray()
					?? Array.Empty<string>());
		}

		public TypeRelationshipResult GetTypeRelationships(string identifier, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			if (symbol.Entity is not ITypeDefinition type)
				throw new InvalidOperationException("Type relationships require a type symbol.");
			var bases = type.DirectBaseTypes.Where(t => t.Kind != TypeKind.Interface).Select(t => t.FullName).ToArray();
			var interfaces = type.DirectBaseTypes.Where(t => t.Kind == TypeKind.Interface).Select(t => t.FullName).ToArray();
			var derived = GetSymbols(cancellationToken).Where(s => s.Entity is ITypeDefinition candidate && candidate.FullName != type.FullName
				&& candidate.DirectBaseTypes.Any(b => b.GetDefinition()?.FullName == type.FullName))
				.Select(s => s.FullName).ToArray();
			var overrides = type.Methods.Where(m => m.IsOverride).Select(m => m.FullName).ToArray();
			return new TypeRelationshipResult(symbol.Match.SymbolId, type.FullName, bases, interfaces, derived, overrides);
		}

		public IReadOnlyList<ReferenceInfo> FindReferences(string identifier, string? kind = null, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			var results = GetIlIndex(cancellationToken).References.Where(reference => reference.TargetSymbolId == symbol.Match.SymbolId
				&& (kind == null || reference.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))).ToList();
			if (symbol.Entity is ITypeDefinition)
			{
				foreach (var source in GetSymbols(cancellationToken))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (kind != null && !kind.Equals("attribute application", StringComparison.OrdinalIgnoreCase))
						continue;
					if (source.Entity.GetAttributes().Any(attribute => attribute.AttributeType.FullName == symbol.FullName))
						results.Add(new ReferenceInfo(source.Match.SymbolId, source.FullName, symbol.Match.SymbolId,
							symbol.FullName, "attribute application", -1, "metadata"));
				}
			}
			return results.OrderBy(reference => reference.SourceSymbolId, StringComparer.Ordinal)
				.ThenBy(reference => reference.Offset).ToArray();
		}

		public Page<ReferenceInfo> SearchReferences(string identifier, string? kind = null, string? cursor = null,
			int limit = DefaultPageSize, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			var matches = FindReferences(symbol.Match.SymbolId, kind, cancellationToken).ToArray();
			string cursorContext = "references|" + symbol.Match.SymbolId + "|" + kind;
			int start = Math.Min(ReadCursor(cursor, cursorContext), matches.Length);
			int pageSize = Math.Clamp(limit, 1, maximumPageSize);
			var page = matches.Skip(start).Take(pageSize).ToArray();
			int next = start + page.Length;
			return new Page<ReferenceInfo>(page, next < matches.Length ? WriteCursor(next, cursorContext) : null,
				next >= matches.Length, matches.Length);
		}

		public IReadOnlyList<InstructionInfo> Disassemble(string identifier, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			return GetIlIndex(cancellationToken).Instructions.TryGetValue(symbol.Match.SymbolId, out var instructions)
				? instructions : Array.Empty<InstructionInfo>();
		}

		public Page<InstructionInfo> DisassemblePage(string identifier, string? cursor = null, int limit = DefaultPageSize,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			var instructions = GetIlIndex(cancellationToken).Instructions.TryGetValue(symbol.Match.SymbolId, out var body)
				? body : Array.Empty<InstructionInfo>();
			string cursorContext = "instructions|" + symbol.Match.SymbolId;
			int start = Math.Min(ReadCursor(cursor, cursorContext), instructions.Count);
			int pageSize = Math.Clamp(limit, 1, maximumPageSize);
			var page = instructions.Skip(start).Take(pageSize).ToArray();
			int next = start + page.Length;
			return new Page<InstructionInfo>(page, next < instructions.Count ? WriteCursor(next, cursorContext) : null,
				next >= instructions.Count, instructions.Count);
		}

		public ControlFlowGraph GetControlFlowGraph(string identifier, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			if (!GetIlIndex(cancellationToken).Methods.TryGetValue(symbol.Match.SymbolId, out var method))
				throw new InvalidOperationException("The selected symbol has no IL method body.");
			var graph = method.ControlFlowGraph;
			var blocks = graph.Blocks.Take(maximumGraphNodes).ToArray();
			int omitted = graph.Blocks.Count - blocks.Length;
			return graph with { Blocks = blocks, IsTruncated = omitted > 0, OmittedBlockCount = omitted };
		}

		public CallGraphResult GetCallGraph(string identifier, string direction = "both", int depth = 3, int maxNodes = 200,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var start = FindSymbol(identifier, cancellationToken);
			depth = Math.Clamp(depth, 0, maximumGraphDepth);
			maxNodes = Math.Clamp(maxNodes, 1, maximumGraphNodes);
			direction = direction.ToLowerInvariant();
			if (direction is not ("callers" or "callees" or "both"))
				throw new ArgumentException("Direction must be callers, callees, or both.", nameof(direction));
			var index = GetIlIndex(cancellationToken);
			var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal) {
				[start.Match.SymbolId] = new GraphNode(start.Match.SymbolId, start.Match.FullName, 0)
			};
			var omittedNodes = new HashSet<string>(StringComparer.Ordinal);
			var frontier = new Queue<(string Id, int Depth)>();
			frontier.Enqueue((start.Match.SymbolId, 0));
			while (frontier.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var current = frontier.Dequeue();
				var adjacent = GetAdjacentEdges(index, direction, current.Id);
				foreach (var edge in adjacent)
				{
					string other = edge.SourceSymbolId == current.Id ? edge.TargetSymbolId : edge.SourceSymbolId;
					string otherName = edge.SourceSymbolId == current.Id ? edge.TargetName : edge.SourceName;
					if (nodes.ContainsKey(other))
						continue;
					if (current.Depth >= depth || nodes.Count >= maxNodes)
					{
						omittedNodes.Add(other);
						continue;
					}
					nodes.Add(other, new GraphNode(other, otherName, current.Depth + 1));
					frontier.Enqueue((other, current.Depth + 1));
				}
			}
			var visibleEdges = index.CallEdges.Where(e => nodes.ContainsKey(e.SourceSymbolId) && nodes.ContainsKey(e.TargetSymbolId)).ToArray();
			var keptEdges = visibleEdges.Take(maxNodes * 8).ToArray();
			int omittedEdgeCount = visibleEdges.Length - keptEdges.Length;
			var unresolvedCalls = index.UnresolvedCalls.Where(call => nodes.ContainsKey(call.SourceSymbolId)).ToArray();
			return new CallGraphResult(nodes.Values.OrderBy(n => n.Depth).ThenBy(n => n.Name, StringComparer.Ordinal).ToArray(),
				keptEdges, omittedNodes.Count > 0 || omittedEdgeCount > 0, omittedNodes.Count, omittedEdgeCount, unresolvedCalls);
		}

		static IReadOnlyList<CallEdge> GetAdjacentEdges(IlIndex index, string direction, string symbolId)
		{
			var outgoing = index.CallsBySource.GetValueOrDefault(symbolId) ?? Array.Empty<CallEdge>();
			if (direction == "callees")
				return outgoing;
			var incoming = index.CallsByTarget.GetValueOrDefault(symbolId) ?? Array.Empty<CallEdge>();
			if (direction == "callers")
				return incoming;
			return outgoing.Concat(incoming).Distinct().ToArray();
		}

		public CallPathResult FindCallPaths(string fromIdentifier, string toIdentifier, int maxDepth = 8,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var from = FindSymbol(fromIdentifier, cancellationToken);
			var to = FindSymbol(toIdentifier, cancellationToken);
			if (from.Match.SymbolId == to.Match.SymbolId)
				return new CallPathResult(true, Array.Empty<CallEdge>(), true);
			var index = GetIlIndex(cancellationToken);
			int depthLimit = Math.Clamp(maxDepth, 0, maximumGraphDepth);
			var queue = new Queue<string>();
			var parent = new Dictionary<string, CallEdge>(StringComparer.Ordinal);
			var visited = new HashSet<string>(StringComparer.Ordinal) { from.Match.SymbolId };
			queue.Enqueue(from.Match.SymbolId);
			var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [from.Match.SymbolId] = 0 };
			bool hitDepthLimit = false;
			while (queue.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string current = queue.Dequeue();
				if (current == to.Match.SymbolId)
					break;
				if (depth[current] >= depthLimit)
				{
					if (index.CallsBySource.GetValueOrDefault(current)?.Any(edge => !visited.Contains(edge.TargetSymbolId)) == true)
						hitDepthLimit = true;
					continue;
				}
				foreach (var edge in index.CallsBySource.GetValueOrDefault(current) ?? Array.Empty<CallEdge>())
				{
					if (visited.Add(edge.TargetSymbolId))
					{
						parent[edge.TargetSymbolId] = edge;
						depth[edge.TargetSymbolId] = depth[current] + 1;
						queue.Enqueue(edge.TargetSymbolId);
					}
				}
			}
			if (!visited.Contains(to.Match.SymbolId))
				return new CallPathResult(false, Array.Empty<CallEdge>(), !hitDepthLimit);
			var result = new List<CallEdge>();
			for (string current = to.Match.SymbolId; current != from.Match.SymbolId; current = parent[current].SourceSymbolId)
				result.Add(parent[current]);
			result.Reverse();
			return new CallPathResult(true, result, true);
		}

		public IReadOnlyList<FeatureFinding> FindFeatures(string category, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var patterns = FeaturePatterns.GetValueOrDefault(category.ToLowerInvariant());
			if (patterns == null)
				throw new ArgumentException("Feature category must be one of networking, storage, process, cryptography, reflection, serialization, or native.", nameof(category));
			var results = new List<FeatureFinding>();
			var index = GetIlIndex(cancellationToken);
			foreach (var instruction in index.AllReferences)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string evidence = instruction.TargetName ?? instruction.Literal ?? string.Empty;
				string? match = patterns.FirstOrDefault(p => evidence.Contains(p, StringComparison.OrdinalIgnoreCase));
				if (match != null)
					results.Add(new FeatureFinding(category, evidence, instruction.SourceSymbolId, instruction.SourceName,
						instruction.Offset, "heuristic"));
			}
			if (category.Equals("native", StringComparison.OrdinalIgnoreCase))
			{
				results.AddRange(GetSymbols(cancellationToken).Where(s => s.Entity is IMethod && IsPInvokeMethod(s))
					.Select(s => new FeatureFinding(category, "P/Invoke method", s.Match.SymbolId, s.Match.FullName, 0, "metadata")));
			}
			return results.DistinctBy(f => (f.SymbolId, f.Offset, f.Evidence)).ToArray();
		}

		public Page<FeatureFinding> SearchFeatures(string category, string? cursor = null, int limit = DefaultPageSize,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var matches = FindFeatures(category, cancellationToken).ToArray();
			string cursorContext = "features|" + category;
			int start = Math.Min(ReadCursor(cursor, cursorContext), matches.Length);
			int pageSize = Math.Clamp(limit, 1, maximumPageSize);
			var page = matches.Skip(start).Take(pageSize).ToArray();
			int next = start + page.Length;
			return new Page<FeatureFinding>(page, next < matches.Length ? WriteCursor(next, cursorContext) : null,
				next >= matches.Length, matches.Length);
		}

		public IReadOnlyList<ResourceInfo> ListResources(string assemblyId, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var module = GetModule(assemblyId);
			var resources = new List<ResourceInfo>();
			foreach (var resource in module.File.Resources)
			{
				cancellationToken.ThrowIfCancellationRequested();
				resources.Add(new ResourceInfo(resource.Name, resource.ResourceType.ToString(), resource.TryGetLength(),
					resource.Attributes == ManifestResourceAttributes.Public));
				if (!resource.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
					continue;
				if (resource.TryGetLength() is > MaximumResourceContainerSize)
				{
					module.Diagnostics.Add($"Resource '{resource.Name}' is too large to enumerate its entries safely.");
					continue;
				}
				try
				{
					using var stream = resource.TryOpenStream() ?? throw new InvalidDataException("The resource stream is unavailable.");
					using var reader = new ResourceReader(stream);
					var entries = reader.GetEnumerator();
					while (entries.MoveNext())
					{
						cancellationToken.ThrowIfCancellationRequested();
						if (entries.Key is not string entryName)
							continue;
						reader.GetResourceData(entryName, out string typeName, out byte[] data);
						resources.Add(new ResourceInfo(resource.Name + "::" + entryName, typeName, data.LongLength, false));
					}
				}
				catch (Exception ex) when (ex is BadImageFormatException || ex is ArgumentException || ex is InvalidDataException || ex is IOException)
				{
					module.Diagnostics.Add($"Could not enumerate resource entries in '{resource.Name}': {ex.Message}");
				}
			}
			return resources;
		}

		public Page<ResourceInfo> ListResources(string assemblyId, string? cursor, int limit, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var resources = ListResources(assemblyId, cancellationToken).ToArray();
			string cursorContext = "resources|" + assemblyId;
			int start = Math.Min(ReadCursor(cursor, cursorContext), resources.Length);
			int pageSize = Math.Clamp(limit, 1, maximumPageSize);
			var page = resources.Skip(start).Take(pageSize).ToArray();
			int next = start + page.Length;
			return new Page<ResourceInfo>(page, next < resources.Length ? WriteCursor(next, cursorContext) : null,
				next >= resources.Length, resources.Length);
		}

		public Page<SearchEvidence> SearchEvidence(string query, string mode = "symbols", string? assemblyId = null,
			string? cursor = null, int limit = DefaultPageSize, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			if (string.IsNullOrWhiteSpace(query))
				throw new ArgumentException("A search query is required.", nameof(query));
			int pageSize = Math.Clamp(limit, 1, maximumPageSize);
			SearchEvidence[] matches;
			switch (mode.ToLowerInvariant())
			{
				case "symbols":
					matches = GetSymbols(cancellationToken).Where(s => (assemblyId == null || s.Match.AssemblyId == assemblyId)
						&& (s.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)
							|| s.DocumentationId.Contains(query, StringComparison.OrdinalIgnoreCase)))
						.Select(s => new SearchEvidence("symbol", s.FullName, s.Match.SymbolId, s.FullName,
							s.Match.AssemblyId, 0, null)).ToArray();
					break;
				case "strings":
					matches = GetIlIndex(cancellationToken).AllReferences.Where(i => i.Literal != null
						&& (assemblyId == null || GetSymbolAssemblyId(i.SourceSymbolId) == assemblyId)
						&& i.Literal.Contains(query, StringComparison.OrdinalIgnoreCase))
						.Select(i => new SearchEvidence("string", i.Literal!, i.SourceSymbolId, i.SourceName,
							GetSymbolAssemblyId(i.SourceSymbolId), i.Offset, i.OpCode)).ToArray();
					break;
				case "resources":
					var resourceMatches = new List<SearchEvidence>();
					foreach (var module in modules.Values)
					{
						cancellationToken.ThrowIfCancellationRequested();
						if (assemblyId != null && module.Id != assemblyId)
							continue;
						resourceMatches.AddRange(ListResources(module.Id, cancellationToken)
							.Where(resource => resource.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
							.Select(resource => new SearchEvidence("resource", resource.Name, string.Empty, resource.Name, module.Id, 0, null)));
					}
					matches = resourceMatches.ToArray();
					break;
				case "code":
					var methods = GetSymbols(cancellationToken).Where(s => s.Entity is IMethod { HasBody: true }
						&& (assemblyId == null || s.Match.AssemblyId == assemblyId)).ToArray();
					matches = methods.Select(s => (Symbol: s, Code: Decompile(s.Match.SymbolId, cancellationToken: cancellationToken).Code))
						.Where(x => x.Code.Contains(query, StringComparison.OrdinalIgnoreCase))
						.Select(x => new SearchEvidence("code", query, x.Symbol.Match.SymbolId, x.Symbol.FullName,
							x.Symbol.Match.AssemblyId, 0, null)).ToArray();
					break;
				case "numbers":
					matches = GetIlIndex(cancellationToken).AllReferences.Where(i => i.Literal != null
						&& (assemblyId == null || GetSymbolAssemblyId(i.SourceSymbolId) == assemblyId)
						&& i.Literal.Equals(query, StringComparison.OrdinalIgnoreCase))
						.Select(i => new SearchEvidence("number", i.Literal!, i.SourceSymbolId, i.SourceName,
							GetSymbolAssemblyId(i.SourceSymbolId), i.Offset, i.OpCode)).ToArray();
					break;
				default:
					throw new ArgumentException("Search mode must be symbols, strings, numbers, resources, or code.", nameof(mode));
			}
			string cursorContext = "evidence|" + mode + "|" + query + "|" + assemblyId;
			int start = Math.Min(ReadCursor(cursor, cursorContext), matches.Length);
			var page = matches.Skip(start).Take(pageSize).ToArray();
			int next = start + page.Length;
			return new Page<SearchEvidence>(page, next < matches.Length ? WriteCursor(next, cursorContext) : null,
				next >= matches.Length, matches.Length);
		}

		public SymbolContext GetSymbolContext(string identifier, CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var symbol = FindSymbol(identifier, cancellationToken);
			var referenceSet = FindReferences(symbol.Match.SymbolId, cancellationToken: cancellationToken).Take(100).ToArray();
			var literals = GetIlIndex(cancellationToken).AllReferences.Where(i => i.SourceSymbolId == symbol.Match.SymbolId && i.Literal != null)
				.Select(i => i.Literal!).Distinct(StringComparer.Ordinal).Take(100).ToArray();
			return new SymbolContext(InspectSymbol(symbol.Match.SymbolId, cancellationToken), Decompile(symbol.Match.SymbolId,
				cancellationToken: cancellationToken), referenceSet, GetCallGraph(symbol.Match.SymbolId, "both", 2, 100, cancellationToken), literals);
		}

		public ResourceData ReadResource(string assemblyId, string name, int maxBytes = MaximumResourcePreview,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			cancellationToken.ThrowIfCancellationRequested();
			maxBytes = Math.Clamp(maxBytes, 1, MaximumResourcePreview);
			var module = GetModule(assemblyId);
			int separator = name.IndexOf("::", StringComparison.Ordinal);
			if (separator >= 0)
			{
				string resourceName = name[..separator];
				string entryName = name[(separator + 2)..];
				var parentResource = module.File.Resources.FirstOrDefault(r => r.Name.Equals(resourceName, StringComparison.Ordinal));
				if (parentResource == null || !parentResource.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
					throw new KeyNotFoundException($"Resource container '{resourceName}' was not found.");
				if (parentResource.TryGetLength() is > MaximumResourceContainerSize)
					throw new InvalidOperationException($"Resource container '{resourceName}' exceeds the safe {MaximumResourceContainerSize}-byte inspection limit.");
				using var resourceStream = parentResource.TryOpenStream() ?? throw new InvalidOperationException("The resource could not be opened.");
				using var reader = new ResourceReader(resourceStream);
				try
				{
					reader.GetResourceData(entryName, out string typeName, out byte[] data);
					return CreateResourcePreview(name, typeName, data, maxBytes);
				}
				catch (ArgumentException ex)
				{
					throw new KeyNotFoundException($"Resource entry '{entryName}' was not found in '{resourceName}'.", ex);
				}
			}
			var resource = module.File.Resources.FirstOrDefault(r => r.Name.Equals(name, StringComparison.Ordinal));
			if (resource == null)
				throw new KeyNotFoundException($"Resource '{name}' was not found.");
			using var stream = resource.TryOpenStream() ?? throw new InvalidOperationException("The resource could not be opened.");
			byte[] bytes = new byte[maxBytes + 1];
			int count = 0;
			while (count < bytes.Length)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int read = stream.Read(bytes, count, bytes.Length - count);
				if (read == 0)
					break;
				count += read;
			}
			bool truncated = count > maxBytes;
			count = Math.Min(count, maxBytes);
			byte[] preview = bytes.AsSpan(0, count).ToArray();
			return CreateResourcePreview(name, resource.ResourceType.ToString(), preview, maxBytes, resource.TryGetLength(), truncated);
		}

		static ResourceData CreateResourcePreview(string name, string mediaType, byte[] data, int maxBytes, long? length = null, bool alreadyTruncated = false)
		{
			bool truncated = alreadyTruncated || data.Length > maxBytes;
			byte[] preview = data.AsSpan(0, Math.Min(data.Length, maxBytes)).ToArray();
			string? text = TryDecodeText(preview);
			return new ResourceData(name, text != null ? "text/plain" : mediaType, text,
				text == null ? Convert.ToBase64String(preview) : null, length ?? data.LongLength, truncated);
		}

		public PackageInfo ListPackageEntries(string path, CancellationToken cancellationToken = default)
		{
			using var operation = EnterWrite();
			cancellationToken.ThrowIfCancellationRequested();
			string fullPath = Path.GetFullPath(path);
			if (!packages.TryGetValue(fullPath, out var package))
			{
				TryLoadInput(fullPath, cancellationToken);
				if (!packages.TryGetValue(fullPath, out package))
					throw new InvalidOperationException("The file is not a supported archive or .NET bundle.");
			}
			return new PackageInfo(fullPath, package.Kind.ToString(), package.Entries.Select(e => new PackageEntryInfo(
				e.Name, e.FullName, e.TryGetLength(), LooksLikeManagedAssembly(e.Name))).ToArray());
		}

		public AssemblyOverview LoadPackageEntry(string packagePath, string entryName, CancellationToken cancellationToken = default)
		{
			using var operation = EnterWrite();
			cancellationToken.ThrowIfCancellationRequested();
			var package = ListPackageEntries(packagePath, cancellationToken);
			var loaded = packages[package.Path];
			var entry = loaded.Entries.FirstOrDefault(e => e.FullName.Equals(entryName, StringComparison.Ordinal)
				|| e.Name.Equals(entryName, StringComparison.Ordinal));
			if (entry == null)
				throw new KeyNotFoundException($"Entry '{entryName}' was not found in the package.");
			var existing = modules.Values.FirstOrDefault(module => module.Path.Equals(entry.PackageQualifiedFileName, StringComparison.Ordinal));
			if (existing != null)
				return CreateOverview(existing, cancellationToken);
			using var stream = entry.TryOpenStream() ?? throw new InvalidOperationException("The package entry could not be opened.");
			return CreateOverview(LoadMetadata(entry.PackageQualifiedFileName, stream, fromPackage: true, cancellationToken));
		}

		public AssemblyComparison CompareAssemblies(string beforeId, string afterId, int limit = 500,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			var before = GetModule(beforeId);
			var after = GetModule(afterId);
			var left = GetSymbols(cancellationToken).Where(s => s.Match.AssemblyId == beforeId).ToDictionary(s => s.DocumentationId, StringComparer.Ordinal);
			var right = GetSymbols(cancellationToken).Where(s => s.Match.AssemblyId == afterId).ToDictionary(s => s.DocumentationId, StringComparer.Ordinal);
			var changes = new List<SymbolChange>();
			foreach (var id in left.Keys.Union(right.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
			{
				cancellationToken.ThrowIfCancellationRequested();
				left.TryGetValue(id, out var oldSymbol);
				right.TryGetValue(id, out var newSymbol);
				if (oldSymbol == null)
					changes.Add(new SymbolChange(newSymbol!.Match.SymbolId, newSymbol.FullName, "added", null, newSymbol.Signature));
				else if (newSymbol == null)
					changes.Add(new SymbolChange(oldSymbol.Match.SymbolId, oldSymbol.FullName, "removed", oldSymbol.Signature, null));
				else if (!oldSymbol.Signature.Equals(newSymbol.Signature, StringComparison.Ordinal))
					changes.Add(new SymbolChange(newSymbol.Match.SymbolId, newSymbol.FullName, "signature_changed", oldSymbol.Signature, newSymbol.Signature));
				else if (oldSymbol.Handle.Kind is HandleKind.MethodDefinition or HandleKind.FieldDefinition
					&& !NormalizeCode(Decompile(oldSymbol.Match.SymbolId, cancellationToken: cancellationToken).Code)
						.Equals(NormalizeCode(Decompile(newSymbol.Match.SymbolId, cancellationToken: cancellationToken).Code), StringComparison.Ordinal))
					changes.Add(new SymbolChange(newSymbol.Match.SymbolId, newSymbol.FullName, "body_changed", oldSymbol.Signature, newSymbol.Signature));
			}
			int cap = Math.Clamp(limit, 1, 2_000);
			return new AssemblyComparison(before.Id, after.Id, changes.Take(cap).ToArray(), changes.Count > cap, changes.Count);
		}

		public ExportResult ExportResource(string assemblyId, string resourceName, string destinationRoot, string relativePath,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			cancellationToken.ThrowIfCancellationRequested();
			string target = ResolveExportPath(destinationRoot, relativePath);
			if (File.Exists(target) || Directory.Exists(target))
				throw new IOException("The export destination already exists.");
			var resource = GetModule(assemblyId).File.Resources.FirstOrDefault(r => r.Name == resourceName)
				?? throw new KeyNotFoundException($"Resource '{resourceName}' was not found.");
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				using (var source = resource.TryOpenStream() ?? throw new InvalidOperationException("The resource could not be opened."))
				using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					byte[] buffer = new byte[81_920];
					int read;
					while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
					{
						cancellationToken.ThrowIfCancellationRequested();
						destination.Write(buffer, 0, read);
					}
				}
				cancellationToken.ThrowIfCancellationRequested();
				File.Move(temporary, target, overwrite: false);
				return new ExportResult(target, 1, new FileInfo(target).Length);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}

		public ExportResult ExportProject(string assemblyId, string destinationRoot, string directoryName,
			CancellationToken cancellationToken = default)
		{
			using var operation = EnterRead();
			cancellationToken.ThrowIfCancellationRequested();
			string target = ResolveExportPath(destinationRoot, directoryName);
			if (File.Exists(target) || Directory.Exists(target))
				throw new IOException("The export destination already exists.");
			string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
			Directory.CreateDirectory(temporary);
			try
			{
				var module = GetModule(assemblyId);
				var resolver = CreateResolver(module);
				var decompiler = new WholeProjectDecompiler(resolver) { MaxDegreeOfParallelism = 1 };
				decompiler.DecompileProject(module.File, temporary);
				cancellationToken.ThrowIfCancellationRequested();
				int files = Directory.GetFiles(temporary, "*", SearchOption.AllDirectories).Length;
				long bytes = Directory.GetFiles(temporary, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				Directory.Move(temporary, target);
				return new ExportResult(target, files, bytes);
			}
			finally
			{
				if (Directory.Exists(temporary))
					Directory.Delete(temporary, recursive: true);
			}
		}

		internal IReadOnlyList<IndexedSymbol> GetSymbols(CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			foreach (var module in modules.Values)
				module.EnsureUnchanged();
			if (symbols != null)
				return symbols;
			lock (indexLock)
			{
				if (symbols != null)
					return symbols;
				var result = new List<IndexedSymbol>();
				foreach (var module in modules.Values)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var typeSystem = CreateDecompiler(module, cancellationToken).TypeSystem;
					foreach (var type in typeSystem.MainModule.TypeDefinitions)
					{
						cancellationToken.ThrowIfCancellationRequested();
						if (!type.MetadataToken.IsNil)
							result.Add(CreateIndexedSymbol(module, type));
						foreach (var member in type.Members)
						{
							if (!member.MetadataToken.IsNil)
								result.Add(CreateIndexedSymbol(module, member));
						}
					}
				}
				symbols = result.OrderBy(s => s.Match.AssemblyId, StringComparer.Ordinal)
					.ThenBy(s => s.Match.MetadataToken).ToArray();
				return symbols;
			}
		}

		internal IReadOnlyList<LoadedModule> LoadedModules => modules.Values.ToArray();

		internal CSharpDecompiler CreateDecompiler(LoadedModule module, CancellationToken cancellationToken = default)
		{
			var resolver = CreateResolver(module);
			return new CSharpDecompiler(module.File, resolver, new DecompilerSettings()) { CancellationToken = cancellationToken };
		}

		internal string GetSymbolId(MetadataFile? file, EntityHandle handle)
		{
			if (file == null)
				return string.Empty;
			var module = modules.Values.FirstOrDefault(m => ReferenceEquals(m.File, file)
				|| Path.GetFullPath(m.Path).Equals(Path.GetFullPath(file.FileName), PathComparison));
			if (module == null)
				return string.Empty;
			return SymbolId(module, MetadataTokens.GetToken(handle));
		}

		internal LoadedModule GetModule(string assemblyId)
		{
			var module = modules.Values.FirstOrDefault(m => m.Id == assemblyId)
				?? throw new KeyNotFoundException($"Assembly '{assemblyId}' is not loaded in this workspace.");
			module.EnsureUnchanged();
			return module;
		}

		internal IndexedSymbol FindSymbol(string identifier, CancellationToken cancellationToken = default)
		{
			ResolveSymbol(identifier, cancellationToken);
			return GetSymbols(cancellationToken).First(s => s.Match.SymbolId.Equals(identifier, StringComparison.Ordinal)
				|| s.DocumentationId.Equals(identifier, StringComparison.Ordinal)
				|| s.FullName.Equals(identifier, StringComparison.Ordinal)
				|| s.Match.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase)
				|| s.FullName.EndsWith("." + identifier, StringComparison.OrdinalIgnoreCase));
		}

		internal IlIndex GetIlIndex(CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (ilIndex != null)
				return ilIndex;
			lock (indexLock)
			{
				return ilIndex ??= IlIndex.Build(this, cancellationToken);
			}
		}

		internal SymbolMatch CreateSymbol(LoadedModule module, IEntity entity)
		{
			var indexed = CreateIndexedSymbol(module, entity);
			return indexed.Match;
		}

		IndexedSymbol CreateIndexedSymbol(LoadedModule module, IEntity entity)
		{
			int token = MetadataTokens.GetToken(entity.MetadataToken);
			string id = SymbolId(module, token);
			string documentationId = module.File.GetIdString(entity.MetadataToken);
			string fullName = entity.FullName;
			string kind = entity switch {
				ITypeDefinition => "type",
				IMethod => "method",
				IField => "field",
				IProperty => "property",
				IEvent => "event",
				_ => "member"
			};
			return new IndexedSymbol(new SymbolMatch(id, entity.Name, kind, fullName, entity.ToString() ?? fullName,
				documentationId, token, module.Id, module.File.Name), module, entity.MetadataToken, entity, fullName, documentationId);
		}

		AssemblyOverview CreateOverview(LoadedModule module, CancellationToken cancellationToken = default)
		{
			var md = module.File.Metadata;
			var assemblyName = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : module.File.Name;
			var assemblyReferences = md.IsAssembly ? module.File.AssemblyReferences.ToArray() : Array.Empty<IAssemblyReference>();
			var references = assemblyReferences.Select(reference => reference.Name).ToArray();
			var diagnostics = module.Diagnostics.ToList();
			var resolver = CreateResolver(module);
			foreach (var reference in assemblyReferences)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					if (resolver.Resolve(reference) == null)
						diagnostics.Add($"Assembly reference '{reference.FullName}' could not be resolved. Add its directory with --reference-dir or load the dependency into this workspace.");
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException || ex is ArgumentException)
				{
					diagnostics.Add($"Assembly reference '{reference.FullName}' could not be resolved: {ex.Message}");
				}
			}
			string? entryPoint = null;
			var corHeader = module.File.CorHeader;
			if (corHeader != null && corHeader.EntryPointTokenOrRelativeVirtualAddress > 0
				&& (corHeader.Flags & System.Reflection.PortableExecutable.CorFlags.NativeEntryPoint) == 0)
			{
				try
				{
					var entity = CreateDecompiler(module).TypeSystem.MainModule.ResolveEntity(MetadataTokens.EntityHandle(corHeader.EntryPointTokenOrRelativeVirtualAddress));
					entryPoint = entity?.FullName;
				}
				catch (BadImageFormatException)
				{
					entryPoint = null;
				}
			}
			Guid mvid = md.GetModuleDefinition().Mvid.IsNil ? Guid.Empty : md.GetGuid(md.GetModuleDefinition().Mvid);
			string architecture = module.File.Reader.PEHeaders.CoffHeader.Machine.ToString();
			return new AssemblyOverview(module.Id, module.Path, assemblyName, module.File.FullName,
				module.File.DetectTargetFrameworkId(), architecture, module.ContentHash, mvid.ToString("D"), entryPoint,
				references, ListResources(module.Id, cancellationToken), diagnostics.Distinct(StringComparer.Ordinal).ToArray());
		}

		LoadedModule LoadMetadata(string path, Stream stream, bool fromPackage = false, CancellationToken cancellationToken = default)
		{
			byte[] bytes;
			using (var buffer = new MemoryStream())
			{
				byte[] chunk = new byte[81_920];
				int read;
				while ((read = stream.Read(chunk, 0, chunk.Length)) != 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
					buffer.Write(chunk, 0, read);
				}
				bytes = buffer.ToArray();
			}
			string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
			var ownedStream = new MemoryStream(bytes, writable: false);
			LoadResult? loaded = null;
			foreach (var loader in loaderRegistry.RegisteredLoaders)
			{
				if (fromPackage && loader is (WebCilFileLoader or BundleFileLoader or ArchiveFileLoader))
					continue;
				ownedStream.Position = 0;
				loaded = loader.Load(path, ownedStream, new FileLoadContext(false, null)).GetAwaiter().GetResult();
				if (loaded?.MetadataFile != null)
					break;
			}
			if (loaded?.MetadataFile is not PEFile peFile)
			{
				ownedStream.Dispose();
				throw new BadImageFormatException($"'{path}' does not contain a supported managed PE assembly.");
			}
			string id = hash[..12] + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..6];
			var module = new LoadedModule(id, path, peFile, ownedStream, hash);
			modules[id] = module;
			if (peFile.Metadata.IsAssembly)
			{
				string assemblyName = peFile.Metadata.GetString(peFile.Metadata.GetAssemblyDefinition().Name);
				if (!modulesByAssemblyName.TryGetValue(assemblyName, out var sameNameModules))
					modulesByAssemblyName.Add(assemblyName, sameNameModules = new List<LoadedModule>());
				sameNameModules.Add(module);
			}
			revision++;
			symbols = null;
			ilIndex = null;
			return module;
		}

		void TryLoadInput(string path, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string fullPath = Path.GetFullPath(path);
			if (modules.Values.Any(m => Path.GetFullPath(m.Path).Equals(fullPath, PathComparison)))
				return;
			if (packages.ContainsKey(fullPath))
				return;
			try
			{
				using var stream = File.OpenRead(fullPath);
				foreach (var loader in loaderRegistry.RegisteredLoaders)
				{
					cancellationToken.ThrowIfCancellationRequested();
					stream.Position = 0;
					var result = loader.Load(fullPath, stream, new FileLoadContext(false, null)).GetAwaiter().GetResult();
					if (result == null)
						continue;
					if (result.Package != null)
					{
						packages[fullPath] = result.Package;
						return;
					}
					if (result.MetadataFile is PEFile peFile)
					{
						peFile.Dispose();
						stream.Position = 0;
						LoadMetadata(fullPath, stream, cancellationToken: cancellationToken);
						return;
					}
					result.MetadataFile?.Dispose();
				}
				inputDiagnostics.Add($"'{fullPath}' is not a supported managed assembly or archive.");
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException
				|| ex is InvalidDataException || ex is OverflowException || ex is ArgumentException || ex is InvalidOperationException)
			{
				inputDiagnostics.Add($"Could not inspect '{fullPath}': {ex.Message}");
			}
		}

		static IEnumerable<string> EnumerateAssemblies(string directory, CancellationToken cancellationToken)
		{
			int count = 0;
			foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (LooksLikeManagedAssembly(path) || IsPackagePath(path))
				{
					if (++count > MaximumDirectoryAssemblies)
						throw new InvalidOperationException($"The directory contains more than {MaximumDirectoryAssemblies} candidate files.");
					yield return path;
				}
			}
		}

		IEnumerable<string> GetResolverDirectories(LoadedModule module)
		{
			if (Path.GetDirectoryName(module.Path) is { Length: > 0 } ownDirectory)
				yield return ownDirectory;
			foreach (string path in modules.Values.Select(m => Path.GetDirectoryName(m.Path)).Where(p => !string.IsNullOrEmpty(p)).Cast<string>())
				yield return path;
			foreach (string path in referenceDirectories)
				yield return path;
		}

		WorkspaceAssemblyResolver CreateResolver(LoadedModule module)
		{
			var resolver = new WorkspaceAssemblyResolver(this, module.Path, module.File.DetectTargetFrameworkId());
			foreach (string directory in GetResolverDirectories(module).Distinct(PathComparer))
				resolver.AddSearchDirectory(directory);
			return resolver;
		}

		MetadataFile? ResolveWorkspaceAssembly(IAssemblyReference reference)
		{
			if (modulesByAssemblyName.TryGetValue(reference.Name, out var candidates))
			{
				foreach (var module in candidates)
				{
					if (MatchesAssemblyReference(module.File, reference))
						return module.File;
				}
			}
			lock (packageDependencyLock)
			{
				foreach (var package in packages.Values)
				{
					foreach (var entry in package.Entries)
					{
						if (!Path.GetFileNameWithoutExtension(entry.Name).Equals(reference.Name, StringComparison.OrdinalIgnoreCase)
							|| !LooksLikeManagedAssembly(entry.Name))
							continue;
						if (packageDependencies.TryGetValue(entry.PackageQualifiedFileName, out var cached))
						{
							if (MatchesAssemblyReference(cached.File, reference))
								return cached.File;
							continue;
						}
						Stream? stream = null;
						PEFile? file = null;
						try
						{
							stream = entry.TryOpenStream();
							if (stream == null)
								continue;
							file = new PEFile(entry.PackageQualifiedFileName, stream, PEStreamOptions.PrefetchEntireImage);
							if (!MatchesAssemblyReference(file, reference))
							{
								file.Dispose();
								stream.Dispose();
								continue;
							}
							packageDependencies.Add(entry.PackageQualifiedFileName, new PackageDependency(file, stream));
							return file;
						}
						catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is InvalidDataException
							|| ex is ArgumentException || ex is OverflowException)
						{
							file?.Dispose();
							stream?.Dispose();
							inputDiagnostics.Add($"Could not resolve package dependency '{entry.FullName}': {ex.Message}");
						}
					}
				}
			}
			return null;
		}

		static bool MatchesAssemblyReference(PEFile file, IAssemblyReference reference)
		{
			var metadata = file.Metadata;
			if (!metadata.IsAssembly)
				return false;
			var definition = metadata.GetAssemblyDefinition();
			if (!metadata.GetString(definition.Name).Equals(reference.Name, StringComparison.OrdinalIgnoreCase))
				return false;
			if (reference.Version != null && definition.Version != reference.Version)
				return false;
			string culture = definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture);
			return string.IsNullOrEmpty(reference.Culture) || culture.Equals(reference.Culture, StringComparison.OrdinalIgnoreCase);
		}

		IndexedSymbol FindSymbolByToken(LoadedModule module, EntityHandle handle)
		{
			string id = SymbolId(module, MetadataTokens.GetToken(handle));
			return GetSymbols().FirstOrDefault(s => s.Match.SymbolId == id)
				?? throw new KeyNotFoundException($"No indexed symbol exists for metadata token 0x{MetadataTokens.GetToken(handle):X8}.");
		}

		static string SymbolId(LoadedModule module, int token)
		{
			var metadata = module.File.Metadata;
			Guid mvid = metadata.GetModuleDefinition().Mvid.IsNil ? Guid.Empty : metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
			return module.Id + ":" + module.File.FullName + ":" + mvid.ToString("D") + ":0x" + token.ToString("X8");
		}

		static bool IsPInvokeMethod(IndexedSymbol symbol) => symbol.Handle.Kind == HandleKind.MethodDefinition
			&& (symbol.Module.File.Metadata.GetMethodDefinition((MethodDefinitionHandle)symbol.Handle).Attributes & MethodAttributes.PinvokeImpl) != 0;

		string GetSymbolAssemblyId(string symbolId) => symbolId.Split(':', 2)[0];

		static bool LooksLikeManagedAssembly(string path) => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)
			|| Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

		static bool IsPackagePath(string path) => Path.GetExtension(path) is { } extension
			&& (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".apk", StringComparison.OrdinalIgnoreCase)
				|| extension.Equals(".xap", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bundle", StringComparison.OrdinalIgnoreCase));

		static string ResolveExportPath(string root, string relative)
		{
			if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
				throw new ArgumentException("An explicit relative export path is required.", nameof(relative));
			string fullRoot = Path.GetFullPath(root);
			string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
			string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			if (!fullPath.StartsWith(prefix, PathComparison))
				throw new UnauthorizedAccessException("The export path must remain inside the configured export root.");
			return fullPath;
		}

		static string? TryDecodeText(byte[] bytes)
		{
			try
			{
				string value = new UTF8Encoding(false, true).GetString(bytes);
				return value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') ? null : value;
			}
			catch (DecoderFallbackException)
			{
				return null;
			}
		}

		int ReadCursor(string? cursor, string context)
		{
			if (cursor == null)
				return 0;
			try
			{
				string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
				if (parts.Length != 3 || !int.TryParse(parts[0], out int cursorRevision) || cursorRevision != revision
					|| !int.TryParse(parts[1], out int offset) || parts[2] != GetCursorContextHash(context))
					throw new FormatException();
				return Math.Max(0, offset);
			}
			catch (FormatException)
			{
				throw new ArgumentException("The cursor is invalid or stale. Start a new search.", nameof(cursor));
			}
		}

		string WriteCursor(int offset, string context) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
			$"{revision}:{offset}:{GetCursorContextHash(context)}"));

		static string GetCursorContextHash(string context) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)))[..16];

		int ReadSourceCursor(string? cursor, string symbolId)
		{
			if (cursor == null)
				return 0;
			try
			{
				string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
				if (parts.Length != 4 || !int.TryParse(parts[0], out int cursorRevision) || cursorRevision != revision
					|| parts[1] != "source" || parts[2] != symbolId || !int.TryParse(parts[3], out int offset))
					throw new FormatException();
				return Math.Max(0, offset);
			}
			catch (FormatException)
			{
				throw new ArgumentException("The source cursor is invalid or stale. Request the source again.", nameof(cursor));
			}
		}

		string WriteSourceCursor(string symbolId, int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{revision}|source|{symbolId}|{offset}"));

		static (string Code, int SourceCharacters) FormatSourcePage(string source, int offset, int maxCharacters)
		{
			var result = new StringBuilder(Math.Min(maxCharacters, source.Length - offset));
			int line = 1 + CountLineBreaks(source, offset);
			int position = offset;
			bool startOfLine = true;
			while (position < source.Length)
			{
				if (startOfLine)
				{
					string prefix = line.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ": ";
					if (result.Length + prefix.Length > maxCharacters)
						break;
					result.Append(prefix);
					startOfLine = false;
				}
				if (result.Length == maxCharacters)
					break;
				char character = source[position++];
				result.Append(character);
				if (character == '\n')
				{
					line++;
					startOfLine = true;
				}
			}
			return (result.ToString(), position - offset);
		}

		static int CountLineBreaks(string source, int length)
		{
			int count = 0;
			for (int i = 0; i < length; i++)
			{
				if (source[i] == '\n')
					count++;
			}
			return count;
		}

		static string NormalizeCode(string code) => string.Join("\n", code.Split('\n').Select(line => {
			int separator = line.IndexOf(": ", StringComparison.Ordinal);
			if (separator > 0 && line.AsSpan(0, separator).IndexOfAnyExcept("0123456789") < 0)
				line = line[(separator + 2)..];
			return line.Trim();
		}).Where(line => line.Length > 0));

		static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

		LockHandle EnterRead()
		{
			lifecycleLock.EnterReadLock();
			if (disposed)
			{
				lifecycleLock.ExitReadLock();
				throw new ObjectDisposedException(nameof(AnalysisWorkspace));
			}
			return new LockHandle(lifecycleLock, write: false);
		}

		LockHandle EnterWrite()
		{
			lifecycleLock.EnterWriteLock();
			if (disposed)
			{
				lifecycleLock.ExitWriteLock();
				throw new ObjectDisposedException(nameof(AnalysisWorkspace));
			}
			return new LockHandle(lifecycleLock, write: true);
		}

		static readonly Dictionary<string, string[]> FeaturePatterns = new(StringComparer.OrdinalIgnoreCase) {
			["networking"] = ["System.Net", "HttpClient", "WebRequest", "Socket", "TcpClient", "UdpClient"],
			["storage"] = ["System.IO.File", "System.IO.Directory", "FileStream", "DirectoryInfo", "IsolatedStorage"],
			["process"] = ["System.Diagnostics.Process", "ProcessStartInfo", "Process.Start", "CreateProcess"],
			["cryptography"] = ["System.Security.Cryptography", "Aes", "RSA", "SHA256", "HMAC"],
			["reflection"] = ["System.Reflection", "GetMethod", "GetType", "Activator.CreateInstance", "Assembly.Load"],
			["serialization"] = ["System.Text.Json", "XmlSerializer", "DataContractSerializer", "BinaryFormatter", "JsonConvert"],
			["native"] = ["DllImport", "LibraryImport", "GetProcAddress", "LoadLibrary"]
		};

		public void Dispose()
		{
			lifecycleLock.EnterWriteLock();
			try
			{
				if (disposed)
					return;
				disposed = true;
				foreach (var dependency in packageDependencies.Values)
				{
					dependency.File.Dispose();
					dependency.Stream.Dispose();
				}
				foreach (var module in modules.Values)
					module.Dispose();
				foreach (var package in packages.Values)
					package.Dispose();
				modules.Clear();
				modulesByAssemblyName.Clear();
				packages.Clear();
				packageDependencies.Clear();
			}
			finally
			{
				lifecycleLock.ExitWriteLock();
			}
		}

		sealed class LockHandle : IDisposable
		{
			readonly ReaderWriterLockSlim owner;
			readonly bool write;

			public LockHandle(ReaderWriterLockSlim owner, bool write)
			{
				this.owner = owner;
				this.write = write;
			}

			public void Dispose()
			{
				if (write)
					owner.ExitWriteLock();
				else
					owner.ExitReadLock();
			}
		}

		internal sealed record LoadedModule(string Id, string Path, PEFile File, Stream OwnedStream, string ContentHash,
			List<string> Diagnostics, long InitialLength, DateTime InitialLastWriteUtc) : IDisposable
		{
			public LoadedModule(string id, string path, PEFile file, Stream ownedStream, string hash)
				: this(id, path, file, ownedStream, hash, new List<string>(), System.IO.File.Exists(path) ? new FileInfo(path).Length : 0,
					System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue)
			{
			}

			public void EnsureUnchanged()
			{
				if (System.IO.File.Exists(Path) && (new FileInfo(Path).Length != InitialLength
					|| System.IO.File.GetLastWriteTimeUtc(Path) != InitialLastWriteUtc))
					throw new InvalidOperationException($"Assembly '{Path}' changed after it was loaded. Reload the workspace to refresh its analysis index.");
			}

			public void Dispose()
			{
				File.Dispose();
				OwnedStream.Dispose();
			}
		}

		sealed record PackageDependency(PEFile File, Stream Stream);

		sealed class WorkspaceAssemblyResolver : UniversalAssemblyResolver, IAssemblyResolver
		{
			readonly AnalysisWorkspace workspace;

			public WorkspaceAssemblyResolver(AnalysisWorkspace workspace, string modulePath, string targetFramework)
				: base(modulePath, false, targetFramework)
			{
				this.workspace = workspace;
			}

			public new MetadataFile? Resolve(IAssemblyReference reference) => workspace.ResolveWorkspaceAssembly(reference) ?? base.Resolve(reference);

			MetadataFile? IAssemblyResolver.Resolve(IAssemblyReference reference) => Resolve(reference);

			MetadataFile? IAssemblyResolver.ResolveModule(MetadataFile mainModule, string moduleName) => base.ResolveModule(mainModule, moduleName);

			Task<MetadataFile?> IAssemblyResolver.ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

			Task<MetadataFile?> IAssemblyResolver.ResolveModuleAsync(MetadataFile mainModule, string moduleName) =>
				Task.FromResult(base.ResolveModule(mainModule, moduleName));

			IDisposable? IAssemblyResolver.BeginSnapshot() => base.BeginSnapshot();
		}

		internal sealed record IndexedSymbol(SymbolMatch Match, LoadedModule Module, EntityHandle Handle,
			IEntity Entity, string FullName, string DocumentationId)
		{
			public string Name => Match.Name;
			public string Signature => Match.Signature;
		}
	}
}
