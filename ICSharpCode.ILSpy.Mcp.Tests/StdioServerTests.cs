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

#nullable enable
#pragma warning disable MCPEXP002

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using NUnit.Framework;

namespace ICSharpCode.ILSpy.Mcp.Tests
{
	public class StdioServerTests
	{
		[Test]
		public async Task StdioServer_InitializesDiscoversAndServesAnalysisRequests()
		{
			string serverPath = Path.Combine(AppContext.BaseDirectory, "ICSharpCode.ILSpy.Mcp.dll");
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			string exportRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			string cancellationInputRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(exportRoot);
			Directory.CreateDirectory(cancellationInputRoot);
			for (int i = 0; i < 5_000; i++)
				File.WriteAllBytes(Path.Combine(cancellationInputRoot, $"candidate-{i:D4}.dll"), Array.Empty<byte>());
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			var transport = new StdioClientTransport(new StdioClientTransportOptions {
				Command = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
				Arguments = [serverPath, "--root", AppContext.BaseDirectory, "--root", cancellationInputRoot, "--export-root", exportRoot],
				Name = "ilspy-mcp-test",
				EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_ROLL_FORWARD"] = "Major" }
			});
			try
			{
				await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
				var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
				var templates = await client.ListResourceTemplatesAsync(cancellationToken: timeout.Token);
				var prompts = await client.ListPromptsAsync(cancellationToken: timeout.Token);

				Assert.That(tools.Select(tool => tool.Name), Does.Contain("open_workspace"));
				Assert.That(tools.Select(tool => tool.Name), Does.Contain("find_references"));
				Assert.That(templates.Select(template => template.ProtocolResourceTemplate.UriTemplate),
					Does.Contain("ilspy://workspaces/{workspaceId}/assemblies/{assemblyId}/overview"));
				Assert.That(prompts.Select(prompt => prompt.Name), Does.Contain("trace_feature"));

				var opened = await client.CallToolAsync("open_workspace", new Dictionary<string, object?> {
					["paths"] = new[] { assemblyPath }
				}, cancellationToken: timeout.Token);
				Assert.That(opened.IsError, Is.Not.True);
				Assert.That(opened.StructuredContent.HasValue, Is.True);
				string workspaceId = GetStringProperty(opened.StructuredContent!.Value, "workspaceId");
				string assemblyId = GetArrayProperty(opened.StructuredContent.Value, "assemblies")[0]
					.GetProperty("assemblyId").GetString()!;

				var results = await Task.WhenAll(
					client.CallToolAsync("search", new Dictionary<string, object?> {
						["workspaceId"] = workspaceId,
						["query"] = "ResolveEndpoint",
						["mode"] = "symbols"
					}, cancellationToken: timeout.Token).AsTask(),
					client.CallToolAsync("assembly_overview", new Dictionary<string, object?> {
						["workspaceId"] = workspaceId,
						["assemblyId"] = assemblyId
					}, cancellationToken: timeout.Token).AsTask());
				Assert.That(results.All(result => result.IsError != true), Is.True);

				var resource = await client.ReadResourceAsync(
					"ilspy://workspaces/{workspaceId}/assemblies/{assemblyId}/overview",
					new Dictionary<string, object?> { ["workspaceId"] = workspaceId, ["assemblyId"] = assemblyId },
					cancellationToken: timeout.Token);
				Assert.That(resource.Contents.Count, Is.GreaterThan(0));

				var prompt = await client.GetPromptAsync("trace_feature", new Dictionary<string, object?> {
					["workspaceId"] = workspaceId,
					["behavior"] = "endpoint configuration"
				}, cancellationToken: timeout.Token);
				Assert.That(prompt.Messages.Count, Is.GreaterThan(0));

				var cancellationCheck = Stopwatch.StartNew();
				using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
				{
					await Assert.CatchAsync<OperationCanceledException>(async () => await client.CallToolAsync("open_workspace",
							new Dictionary<string, object?> {
								["paths"] = new[] { cancellationInputRoot }
							}, cancellationToken: cancellation.Token));
				}
				var responsive = await client.CallToolAsync("list_assemblies", new Dictionary<string, object?> {
					["workspaceId"] = workspaceId
				}, cancellationToken: timeout.Token);
				Assert.That(responsive.IsError, Is.Not.True);

				var closed = await client.CallToolAsync("close_workspace", new Dictionary<string, object?> {
					["workspaceId"] = workspaceId
				}, cancellationToken: timeout.Token);
				Assert.That(closed.IsError, Is.Not.True);
				Assert.That(cancellationCheck.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
			}
			finally
			{
				Directory.Delete(exportRoot, recursive: true);
				Directory.Delete(cancellationInputRoot, recursive: true);
			}
		}

		static string GetStringProperty(System.Text.Json.JsonElement value, string name) => value.EnumerateObject()
			.Single(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.GetString()!;

		static System.Text.Json.JsonElement[] GetArrayProperty(System.Text.Json.JsonElement value, string name) => value.EnumerateObject()
			.Single(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.EnumerateArray().ToArray();
	}
}
