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
using System.Threading.Tasks;

using ICSharpCode.ILSpy.Mcp.Analysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace ICSharpCode.ILSpy.Mcp
{
	static class Program
	{
		static async Task<int> Main(string[] args)
		{
			ServerOptions options;
			try
			{
				options = ServerOptions.Parse(args);
			}
			catch (ArgumentException ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 64;
			}
			if (options.ShowHelp)
			{
				Console.WriteLine(ServerOptions.Usage);
				return 0;
			}

			var builder = Host.CreateApplicationBuilder(args: Array.Empty<string>());
			builder.Logging.ClearProviders();
			builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
			builder.Services.AddSingleton(new WorkspaceManager(options.InputRoots, options.ExportRoot,
				options.ReferenceDirectories, options.RequestTimeoutSeconds, new AnalysisLimits(options.MaximumPageSize,
					options.MaximumSourceCharacters, options.MaximumGraphDepth, options.MaximumGraphNodes)));
			builder.Services.AddMcpServer()
				.WithStdioServerTransport()
				.WithToolsFromAssembly()
				.WithResourcesFromAssembly()
				.WithPromptsFromAssembly();

			using var host = builder.Build();
			await host.RunAsync().ConfigureAwait(false);
			return 0;
		}
	}

	sealed record ServerOptions(string[] InputRoots, string? ExportRoot, string[] ReferenceDirectories,
		int RequestTimeoutSeconds, int MaximumPageSize, int MaximumSourceCharacters, int MaximumGraphDepth,
		int MaximumGraphNodes, bool ShowHelp)
	{
		public const string Usage = "ilspy-mcp [--root <directory>]... [--export-root <directory>] [--reference-dir <directory>]... [--request-timeout <seconds>] [--max-page-size <count>] [--max-source-characters <count>] [--max-graph-depth <count>] [--max-graph-nodes <count>] [--help]";

		public static ServerOptions Parse(string[] args)
		{
			var roots = new List<string>();
			var references = new List<string>();
			string? exportRoot = null;
			int timeout = 30;
			int maximumPageSize = 200;
			int maximumSourceCharacters = 16_000;
			int maximumGraphDepth = 10;
			int maximumGraphNodes = 2_000;
			bool help = false;
			for (int i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--root":
						roots.Add(ReadValue(args, ref i));
						break;
					case "--export-root":
						exportRoot = ReadValue(args, ref i);
						break;
					case "--reference-dir":
						references.Add(ReadValue(args, ref i));
						break;
					case "--request-timeout":
						timeout = ReadInteger(args, ref i, "--request-timeout", 1, 300);
						break;
					case "--max-page-size":
						maximumPageSize = ReadInteger(args, ref i, "--max-page-size", 1, 10_000);
						break;
					case "--max-source-characters":
						maximumSourceCharacters = ReadInteger(args, ref i, "--max-source-characters", 8, 1_000_000);
						break;
					case "--max-graph-depth":
						maximumGraphDepth = ReadInteger(args, ref i, "--max-graph-depth", 0, 100);
						break;
					case "--max-graph-nodes":
						maximumGraphNodes = ReadInteger(args, ref i, "--max-graph-nodes", 1, 10_000);
						break;
					case "--help":
					case "-h":
						help = true;
						break;
					default:
						throw new ArgumentException($"Unknown option '{args[i]}'. Use --help for usage.");
				}
			}
			if (roots.Count == 0)
				roots.Add(Directory.GetCurrentDirectory());
			return new ServerOptions(roots.ToArray(), exportRoot, references.ToArray(), timeout, maximumPageSize,
				maximumSourceCharacters, maximumGraphDepth, maximumGraphNodes, help);
		}

		static int ReadInteger(string[] args, ref int index, string option, int minimum, int maximum)
		{
			if (!int.TryParse(ReadValue(args, ref index), out int value) || value < minimum || value > maximum)
				throw new ArgumentException($"{option} must be between {minimum} and {maximum}.");
			return value;
		}

		static string ReadValue(string[] args, ref int index)
		{
			if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
				throw new ArgumentException($"Option '{args[index]}' requires a value.");
			return args[++index];
		}
	}
}
