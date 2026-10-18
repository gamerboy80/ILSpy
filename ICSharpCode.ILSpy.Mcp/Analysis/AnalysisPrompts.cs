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

using ModelContextProtocol.Server;

namespace ICSharpCode.ILSpy.Mcp
{
	[McpServerPromptType]
	public static class AnalysisPrompts
	{
		[McpServerPrompt(Name = "orient_application")]
		public static string OrientApplication(string workspaceId) =>
			$"Orient yourself in ILSpy workspace {workspaceId}. Start with list_assemblies and assembly_overview. Identify entry points, major namespaces, external references, and unresolved dependencies. Use evidence from symbol IDs and metadata tokens. Report what the static analysis can and cannot establish.";

		[McpServerPrompt(Name = "trace_feature")]
		public static string TraceFeature(string workspaceId, string behavior) =>
			$"Trace behavior '{behavior}' in ILSpy workspace {workspaceId}. Search strings, symbols, and relevant API features; inspect candidate methods; follow callers and callees with find_references, call_graph, and find_call_paths. Cite assembly IDs, symbol IDs, and IL offsets, and distinguish direct evidence from heuristic conclusions.";

		[McpServerPrompt(Name = "compare_versions")]
		public static string CompareVersions(string workspaceId, string beforeAssemblyId, string afterAssemblyId) =>
			$"Compare assemblies {beforeAssemblyId} and {afterAssemblyId} in ILSpy workspace {workspaceId}. Use compare_assemblies, then inspect changed symbols and their references. Separate API/signature changes from body changes and support conclusions with symbol IDs and source.";
	}
}
