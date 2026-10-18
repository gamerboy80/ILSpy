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
using System.ComponentModel;
using System.Linq;
using System.Threading;

using ICSharpCode.ILSpy.Mcp.Analysis;

using ModelContextProtocol.Server;

namespace ICSharpCode.ILSpy.Mcp
{
	[McpServerToolType]
	public sealed class WorkspaceTools
	{
		readonly WorkspaceManager workspaces;

		public WorkspaceTools(WorkspaceManager workspaces)
		{
			this.workspaces = workspaces;
		}

		[McpServerTool(Name = "open_workspace", UseStructuredContent = true), Description("Open managed assemblies from allowed input roots and create an isolated analysis workspace.")]
		public WorkspaceOpened OpenWorkspace([Description("Files or directories inside configured roots. Omit to scan the configured roots.")] string[]? paths = null,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.OpenWorkspace(paths, timeout.Token);
		}

		[McpServerTool(Name = "list_assemblies", UseStructuredContent = true), Description("List managed assemblies and archives loaded in a workspace.")]
		public WorkspaceAssemblyList ListAssemblies(string workspaceId, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			var workspace = workspaces.GetWorkspace(workspaceId);
			return new WorkspaceAssemblyList(workspace.GetAssemblies(timeout.Token), workspace.GetPackages(timeout.Token));
		}

		[McpServerTool(Name = "close_workspace", UseStructuredContent = true), Description("Close a workspace and release its metadata, streams, and analysis indexes.")]
		public bool CloseWorkspace(string workspaceId) => workspaces.CloseWorkspace(workspaceId);

		[McpServerTool(Name = "list_package_entries", UseStructuredContent = true), Description("List entries in a supported archive or .NET bundle without extracting the package.")]
		public PackageInfo ListPackageEntries(string workspaceId, string path, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.ListPackageEntries(workspaceId, path, timeout.Token);
		}

		[McpServerTool(Name = "load_assembly", UseStructuredContent = true), Description("Load a managed assembly from an allowed path, or load a selected entry from an archive or bundle.")]
		public AssemblyOverview LoadAssembly(string workspaceId, string path, string? entryName = null, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return entryName == null ? workspaces.LoadAssembly(workspaceId, path, timeout.Token)
				: workspaces.LoadPackageEntry(workspaceId, path, entryName, timeout.Token);
		}

		[McpServerTool(Name = "assembly_overview", UseStructuredContent = true), Description("Show assembly identity, target framework, architecture, references, resources, entry point, and input hash.")]
		public AssemblyOverview AssemblyOverview(string workspaceId, string assemblyId, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).GetOverview(assemblyId, timeout.Token);
		}

		[McpServerTool(Name = "list_resources", UseStructuredContent = true), Description("List embedded resources in an assembly. Results are paged.")]
		public Page<ResourceInfo> ListResources(string workspaceId, string assemblyId, string? cursor = null, int limit = 50,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).ListResources(assemblyId, cursor, limit, timeout.Token);
		}

		[McpServerTool(Name = "read_resource", UseStructuredContent = true), Description("Read a bounded text or base64 preview of an embedded resource without deserializing objects.")]
		public ResourceData ReadResource(string workspaceId, string assemblyId, string name, int maxBytes = 32_768,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).ReadResource(assemblyId, name, maxBytes, timeout.Token);
		}

		[McpServerTool(Name = "export_resource", UseStructuredContent = true), Description("Export one embedded resource beneath the explicitly configured export root. Existing files are never overwritten.")]
		public ExportResult ExportResource(string workspaceId, string assemblyId, string resourceName, string relativePath,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.ExportResource(workspaceId, assemblyId, resourceName, relativePath, timeout.Token);
		}

		[McpServerTool(Name = "export_project", UseStructuredContent = true), Description("Decompile an assembly to a project in a new directory beneath the configured export root.")]
		public ExportResult ExportProject(string workspaceId, string assemblyId, string directoryName,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.ExportProject(workspaceId, assemblyId, directoryName, timeout.Token);
		}
	}

	[McpServerToolType]
	public sealed class SymbolTools
	{
		readonly WorkspaceManager workspaces;

		public SymbolTools(WorkspaceManager workspaces)
		{
			this.workspaces = workspaces;
		}

		[McpServerTool(Name = "list_symbols", UseStructuredContent = true), Description("List types and members with stable symbol IDs and documentation IDs. Use a cursor to page through large assemblies.")]
		public Page<SymbolMatch> ListSymbols(string workspaceId, string? query = null, string? assemblyId = null,
			string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).Search(query ?? string.Empty, assemblyId, cursor, limit, timeout.Token);
		}

		[McpServerTool(Name = "resolve_symbol", UseStructuredContent = true), Description("Resolve a symbol ID, documentation ID, or unique full name to its stable assembly and metadata identity.")]
		public SymbolMatch ResolveSymbol(string workspaceId, string identifier, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).ResolveSymbol(identifier, timeout.Token);
		}

		[McpServerTool(Name = "inspect_symbol", UseStructuredContent = true), Description("Inspect a symbol's signature, attributes, declaring type, and direct base types or interfaces.")]
		public SymbolDetails InspectSymbol(string workspaceId, string identifier, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).InspectSymbol(identifier, timeout.Token);
		}

		[McpServerTool(Name = "decompile", UseStructuredContent = true), Description("Decompile one type or member to numbered C# source. Large responses include a continuation cursor.")]
		public DecompiledCode Decompile(string workspaceId, string identifier, string? cursor = null, int maxCharacters = 16_000,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).Decompile(identifier, cursor, maxCharacters, timeout.Token);
		}

		[McpServerTool(Name = "search", UseStructuredContent = true), Description("Search symbol names, strings, numeric constants, embedded resource names, or decompiled method bodies.")]
		public SearchResult Search(string workspaceId, string query, string mode = "symbols", string? assemblyId = null,
			string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			var workspace = workspaces.GetWorkspace(workspaceId);
			var result = workspace.SearchEvidence(query, mode, assemblyId, cursor, limit, timeout.Token);
			var assemblies = workspace.GetAssemblies(timeout.Token).Where(assembly => assemblyId == null || assembly.AssemblyId == assemblyId).ToArray();
			return new SearchResult(result, assemblies.Select(assembly => assembly.AssemblyId).ToArray(),
				assemblies.SelectMany(assembly => assembly.Diagnostics).Distinct(StringComparer.Ordinal).ToArray());
		}

		[McpServerTool(Name = "find_references", UseStructuredContent = true), Description("Find calls, type uses, and field reads or writes for a symbol, including source method and IL offset.")]
		public ReferenceSearchResult FindReferences(string workspaceId, string identifier, string? kind = null, string? cursor = null, int limit = 50,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			var workspace = workspaces.GetWorkspace(workspaceId);
			var results = workspace.SearchReferences(identifier, kind, cursor, limit, timeout.Token);
			return new ReferenceSearchResult(results, workspace.GetAssemblies(timeout.Token).Select(assembly => assembly.AssemblyId).ToArray());
		}

		[McpServerTool(Name = "type_relationships", UseStructuredContent = true), Description("Find direct base types, interfaces, derived types, and overriding methods for a type.")]
		public TypeRelationshipResult TypeRelationships(string workspaceId, string identifier, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).GetTypeRelationships(identifier, timeout.Token);
		}

		[McpServerTool(Name = "symbol_context", UseStructuredContent = true), Description("Bundle a symbol's declaration, bounded source, references, call neighborhood, and relevant string literals for one-step agent context.")]
		public SymbolContext SymbolContext(string workspaceId, string identifier, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			timeout.Token.ThrowIfCancellationRequested();
			return workspaces.GetWorkspace(workspaceId).GetSymbolContext(identifier);
		}
	}

	[McpServerToolType]
	public sealed class FlowTools
	{
		readonly WorkspaceManager workspaces;

		public FlowTools(WorkspaceManager workspaces)
		{
			this.workspaces = workspaces;
		}

		[McpServerTool(Name = "disassemble", UseStructuredContent = true), Description("Decode a method's IL instructions, branch targets, call and field operands, user strings, and exact instruction offsets.")]
		public InstructionSearchResult Disassemble(string workspaceId, string identifier, string? cursor = null, int limit = 50,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return new InstructionSearchResult(workspaces.GetWorkspace(workspaceId).DisassemblePage(identifier, cursor, limit, timeout.Token));
		}

		[McpServerTool(Name = "control_flow_graph", UseStructuredContent = true), Description("Return basic blocks, branch edges, local slots, and exception regions for a method body.")]
		public ControlFlowGraph ControlFlowGraph(string workspaceId, string identifier, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return workspaces.GetWorkspace(workspaceId).GetControlFlowGraph(identifier, timeout.Token);
		}

		[McpServerTool(Name = "call_graph", UseStructuredContent = true), Description("Walk callers, callees, or both from a method with explicit depth and node limits.")]
		public CallGraphResult CallGraph(string workspaceId, string identifier, string direction = "both", int depth = 3, int maxNodes = 200,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			timeout.Token.ThrowIfCancellationRequested();
			return workspaces.GetWorkspace(workspaceId).GetCallGraph(identifier, direction, depth, maxNodes, timeout.Token);
		}

		[McpServerTool(Name = "find_call_paths", UseStructuredContent = true), Description("Find the shortest direct-call path between two loaded methods, bounded by maximum depth.")]
		public CallPathResult FindCallPaths(string workspaceId, string fromIdentifier, string toIdentifier, int maxDepth = 8,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			timeout.Token.ThrowIfCancellationRequested();
			return workspaces.GetWorkspace(workspaceId).FindCallPaths(fromIdentifier, toIdentifier, maxDepth, timeout.Token);
		}

		[McpServerTool(Name = "find_features", UseStructuredContent = true), Description("Search IL references for common networking, storage, process, cryptography, reflection, serialization, or native interop APIs. Findings are heuristic and include evidence locations.")]
		public FeatureSearchResult FindFeatures(string workspaceId, string category, string? cursor = null, int limit = 50,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			timeout.Token.ThrowIfCancellationRequested();
			var findings = workspaces.GetWorkspace(workspaceId).SearchFeatures(category, cursor, limit, timeout.Token);
			return new FeatureSearchResult(findings, "heuristic");
		}

		[McpServerTool(Name = "compare_assemblies", UseStructuredContent = true), Description("Compare two loaded versions by symbol identity, signatures, and normalized decompiled code.")]
		public AssemblyComparison CompareAssemblies(string workspaceId, string beforeAssemblyId, string afterAssemblyId, int limit = 500,
			CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			timeout.Token.ThrowIfCancellationRequested();
			return workspaces.GetWorkspace(workspaceId).CompareAssemblies(beforeAssemblyId, afterAssemblyId, limit, timeout.Token);
		}
	}
}
