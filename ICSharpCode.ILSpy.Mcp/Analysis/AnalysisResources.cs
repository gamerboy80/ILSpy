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

using System.Text.Json;
using System.Threading;

using ICSharpCode.ILSpy.Mcp.Analysis;

using ModelContextProtocol.Server;

namespace ICSharpCode.ILSpy.Mcp
{
	[McpServerResourceType]
	public sealed class AnalysisResources
	{
		readonly WorkspaceManager workspaces;

		public AnalysisResources(WorkspaceManager workspaces)
		{
			this.workspaces = workspaces;
		}

		[McpServerResource(UriTemplate = "ilspy://workspaces/{workspaceId}/assemblies/{assemblyId}/overview",
			Name = "assembly_overview", MimeType = "application/json")]
		public string GetAssemblyOverview(string workspaceId, string assemblyId, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			return JsonSerializer.Serialize(workspaces.GetWorkspace(workspaceId).GetOverview(assemblyId, timeout.Token));
		}

		[McpServerResource(UriTemplate = "ilspy://workspaces/{workspaceId}/symbols/{symbolId}",
			Name = "decompiled_symbol", MimeType = "application/json")]
		public string GetSymbol(string workspaceId, string symbolId, CancellationToken cancellationToken = default)
		{
			using var timeout = workspaces.CreateTimeout(cancellationToken);
			var workspace = workspaces.GetWorkspace(workspaceId);
			var details = workspace.InspectSymbol(symbolId, timeout.Token);
			var source = workspace.Decompile(symbolId, cancellationToken: timeout.Token);
			return JsonSerializer.Serialize(new { details, source });
		}
	}
}
