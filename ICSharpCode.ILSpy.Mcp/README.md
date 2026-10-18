# ILSpy MCP Server

`ilspy-mcp` exposes ILSpy's managed-assembly analysis through the Model Context Protocol. It inspects .NET and Mono assemblies without executing them.

## Install

```sh
dotnet tool install --global ICSharpCode.ILSpy.Mcp
```

## Configure an MCP client

Launch `ilspy-mcp` as a local stdio server. By default, the current directory is the allowed input root. Add `--root <directory>` to allow another input root, `--export-root <directory>` to enable exports into the selected directory, and `--reference-dir <directory>` for dependency resolution. Exports are disabled until an export root is explicitly configured. Use `--request-timeout`, `--max-page-size`, `--max-source-characters`, `--max-graph-depth`, and `--max-graph-nodes` to configure hard request limits; tool arguments can select a smaller value for an individual request.

Example client configuration:

```json
{
  "mcpServers": {
    "ilspy": {
      "command": "ilspy-mcp",
      "args": ["--root", "/path/to/application", "--export-root", "/path/to/analysis-output"]
    }
  }
}
```

Open a workspace, inspect `assembly_overview`, search for a string or API, retrieve the matching method with `decompile`, follow its callers with `call_graph` or `find_call_paths`, then export selected assemblies or resources.

The server defaults to pages of 50 results, 16,000 characters of source, call graph depth 3, 200 graph nodes, and a 30-second request deadline. Hard caps default to 200 results per page, 16,000 source characters, graph depth 10, and 2,000 graph nodes. The corresponding `--max-*` arguments can raise or lower these caps within their documented ranges in `ilspy-mcp --help`. Tool responses include completeness and truncation details. Feature detection is heuristic; reflection and runtime-generated behavior may not appear in static results.
