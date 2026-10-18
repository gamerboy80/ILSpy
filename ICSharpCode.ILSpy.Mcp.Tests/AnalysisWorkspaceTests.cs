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
using System.Linq;
using System.IO;
using System.IO.Compression;

using ICSharpCode.ILSpy.Mcp.Analysis;

using NUnit.Framework;

namespace ICSharpCode.ILSpy.Mcp.Tests
{
	public class AnalysisWorkspaceTests
	{
		[Test]
		public void SearchAndDecompile_ReturnsStableEvidenceForFixtureMethod()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);

			var matches = workspace.SearchSymbols("ResolveEndpoint");
			var method = matches.Single(m => m.Name.EndsWith("ResolveEndpoint", System.StringComparison.Ordinal));
			var code = workspace.Decompile(method.SymbolId);

			Assert.That(method.MetadataToken, Is.GreaterThan(0));
			Assert.That(method.DocumentationId, Does.Contain("ResolveEndpoint"));
			Assert.That(code.Code, Does.Contain("api.example.test"));
			Assert.That(code.Code, Does.StartWith("0001:"));
			Assert.That(code.AssemblyHash, Is.Not.Empty);
		}

		[Test]
		public void NumberedDecompile_RespectsFormattedCharacterLimitAcrossPages()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var method = workspace.SearchSymbols("ResolveEndpoint").Single(symbol => symbol.Kind == "method");

			var first = workspace.Decompile(method.SymbolId, maxCharacters: 8);
			var second = workspace.Decompile(method.SymbolId, first.NextCursor, maxCharacters: 8);

			Assert.That(first.Code.Length, Is.LessThanOrEqualTo(8));
			Assert.That(second.Code.Length, Is.LessThanOrEqualTo(8));
			Assert.That(first.IsTruncated, Is.True);
			Assert.That(second.Code, Does.StartWith("0001:"));
		}

		[Test]
		public void AnalysisLimits_ConfigurePageSourceAndGraphBounds()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath], limits: new AnalysisLimits(
				MaximumPageSize: 1, MaximumSourceCharacters: 8, MaximumGraphDepth: 0, MaximumGraphNodes: 1));
			var method = workspace.SearchSymbols("ResolveEndpoint").Single(symbol => symbol.Kind == "method");
			var branchingMethod = workspace.SearchSymbols("CallCycleA").Single(symbol => symbol.Kind == "method");

			var page = workspace.Search(string.Empty, limit: 20);
			var source = workspace.Decompile(method.SymbolId, maxCharacters: 100);
			var graph = workspace.GetCallGraph(method.SymbolId, depth: 5, maxNodes: 20);
			var controlFlow = workspace.GetControlFlowGraph(branchingMethod.SymbolId);

			Assert.That(page.Items, Has.Length.EqualTo(1));
			Assert.That(source.Code.Length, Is.LessThanOrEqualTo(8));
			Assert.That(graph.Nodes, Has.Length.EqualTo(1));
			Assert.That(controlFlow.IsTruncated, Is.True);
			Assert.That(controlFlow.OmittedBlockCount, Is.GreaterThan(0));
		}

		[Test]
		public void ExportProject_RequiresExplicitExportRoot()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var manager = new WorkspaceManager([Path.GetDirectoryName(assemblyPath)!], null!);
			var opened = manager.OpenWorkspace([assemblyPath]);
			var assembly = opened.Assemblies.Single(a => a.Name == "ICSharpCode.ILSpy.Mcp.Tests");

			Assert.That(() => manager.ExportProject(opened.WorkspaceId, assembly.AssemblyId, "decompiled"),
				Throws.InvalidOperationException.With.Message.Contains("--export-root"));
		}

		[Test]
		public void ExportProject_WritesACompleteProjectWithoutOverwriting()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				using var manager = new WorkspaceManager([Path.GetDirectoryName(assemblyPath)!], root);
				var opened = manager.OpenWorkspace([assemblyPath]);
				string assemblyId = opened.Assemblies.Single(a => a.Name == "ICSharpCode.ILSpy.Mcp.Tests").AssemblyId;
				var exported = manager.ExportProject(opened.WorkspaceId, assemblyId, "decompiled-project");

				Assert.That(exported.FilesWritten, Is.GreaterThan(1));
				Assert.That(File.Exists(Path.Combine(exported.Path, "ICSharpCode.ILSpy.Mcp.Tests.csproj")), Is.True);
				Assert.That(Directory.EnumerateFiles(exported.Path, "*.cs", SearchOption.AllDirectories), Is.Not.Empty);
				Assert.That(() => manager.ExportProject(opened.WorkspaceId, assemblyId, "decompiled-project"),
					Throws.TypeOf<IOException>());
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[Test]
		public void OpenWorkspace_RejectsInputsWithNoSupportedManagedAssemblies()
		{
			string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			string nativePath = Path.Combine(root, "native.dll");
			File.WriteAllText(nativePath, "not a managed assembly");
			try
			{
				using var manager = new WorkspaceManager([root], null);

				Assert.That(() => manager.OpenWorkspace([nativePath]),
					Throws.TypeOf<InvalidOperationException>().With.Message.Contains("native, AOT, or malformed"));
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[Test]
		public void SearchNumbers_ReturnsInstructionLocationForIntegralConstant()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			string assemblyId = workspace.Assemblies.Single(a => a.Name == "ICSharpCode.ILSpy.Mcp.Tests").AssemblyId;

			var result = workspace.SearchEvidence("2718", "numbers", assemblyId);

			var evidence = result.Items.Single(item => item.SymbolName.Contains("AddSentinel", System.StringComparison.Ordinal));
			Assert.That(evidence.Offset, Is.GreaterThanOrEqualTo(0));
			Assert.That(evidence.OpCode, Is.EqualTo("ldc.i4"));

			var zero = workspace.SearchEvidence("0", "numbers", assemblyId);
			Assert.That(zero.Items.Any(item => item.SymbolName.Contains("CallCycleA", System.StringComparison.Ordinal)
				&& item.OpCode == "ldc.i4.0"), Is.True);
		}

		[Test]
		public void FindReferences_MapsCrossAssemblyCallToExactIlLocation()
		{
			string fixturePath = typeof(AnalysisFixture).Assembly.Location;
			string serverPath = typeof(AnalysisWorkspace).Assembly.Location;
			using var workspace = new AnalysisWorkspace([fixturePath, serverPath]);
			var target = workspace.SearchSymbols("ResolveSymbol").Single(symbol =>
				symbol.FullName.EndsWith("AnalysisWorkspace.ResolveSymbol", System.StringComparison.Ordinal));

			var reference = workspace.FindReferences(target.SymbolId).Single(item =>
				item.SourceName.Contains("ResolveInWorkspace", System.StringComparison.Ordinal));

			Assert.That(reference.Offset, Is.GreaterThanOrEqualTo(0));
			Assert.That(reference.Kind, Is.EqualTo("call"));
		}

		[Test]
		public void FindReferences_ClassifiesInterfaceDispatchAndFieldAccesses()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var contractMethod = workspace.SearchSymbols("IFixtureContract.Transform").Single();
			var interfaceCall = workspace.FindReferences(contractMethod.SymbolId).Single(item =>
				item.SourceName.Contains("CallInterface", System.StringComparison.Ordinal));
			var field = workspace.SearchSymbols("Counter").Single(symbol => symbol.Kind == "field");
			var fieldReferences = workspace.FindReferences(field.SymbolId);

			Assert.That(interfaceCall.Kind, Is.EqualTo("virtual call"));
			Assert.That(interfaceCall.Offset, Is.GreaterThanOrEqualTo(0));
			Assert.That(fieldReferences.Select(reference => reference.Kind), Does.Contain("field write"));
			Assert.That(fieldReferences.Select(reference => reference.Kind), Does.Contain("field read"));
		}

		[Test]
		public void Disassemble_DecodesLocalVariableSlots()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var method = workspace.SearchSymbols("SumLocals").Single(symbol => symbol.Kind == "method");

			var instructions = workspace.Disassemble(method.SymbolId);

			Assert.That(instructions.Any(instruction => instruction.OpCode == "ldloc.s" && instruction.Operand == "V_4"), Is.True);
		}

		[Test]
		public void ControlFlowGraph_SeparatesExceptionRegionBoundaries()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var method = workspace.SearchSymbols("ParseOrSentinel").Single(symbol => symbol.Kind == "method");

			var graph = workspace.GetControlFlowGraph(method.SymbolId);
			var region = graph.ExceptionRegions.Single();

			Assert.That(region.Kind, Is.EqualTo("Catch"));
			Assert.That(graph.Blocks.Any(block => block.StartOffset == region.TryOffset), Is.True);
			Assert.That(graph.Blocks.Any(block => block.StartOffset == region.HandlerOffset), Is.True);
		}

		[Test]
		public void FindReferences_ReportsAttributeApplicationsAsMetadataLocations()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var attribute = workspace.SearchSymbols("FixtureMarkerAttribute").Single(symbol => symbol.Kind == "type");

			var application = workspace.FindReferences(attribute.SymbolId).Single(reference =>
				reference.SourceName.EndsWith("MarkedFixture", StringComparison.Ordinal));

			Assert.That(application.Kind, Is.EqualTo("attribute application"));
			Assert.That(application.Offset, Is.EqualTo(-1));
			Assert.That(application.OpCode, Is.EqualTo("metadata"));
		}

		[Test]
		public void CallGraph_ReportsIndirectCallSitesSeparatelyFromDirectEdges()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var method = workspace.SearchSymbols("CallIndirect").Single(symbol => symbol.Kind == "method");
			var instructions = workspace.Disassemble(method.SymbolId);
			var graph = workspace.GetCallGraph(method.SymbolId);

			Assert.That(instructions.Single(instruction => instruction.OpCode == "calli").AccessKind,
				Is.EqualTo("unresolved indirect call"));
			Assert.That(graph.UnresolvedCalls.Single().Offset, Is.GreaterThanOrEqualTo(0));
			Assert.That(graph.Edges.Any(edge => edge.SourceSymbolId == method.SymbolId), Is.False);
		}

		[Test]
		public void CallGraph_ReportsOmittedNodesForCyclesAndHonorsLimits()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var method = workspace.SearchSymbols("CallCycleA").Single(symbol => symbol.Kind == "method");

			var graph = workspace.GetCallGraph(method.SymbolId, "callees", depth: 5, maxNodes: 2);

			Assert.That(graph.Nodes, Has.Length.EqualTo(2));
			Assert.That(graph.IsTruncated, Is.True);
			Assert.That(graph.OmittedNodeCount, Is.EqualTo(1));
			Assert.That(graph.Edges.All(edge => graph.Nodes.Any(node => node.SymbolId == edge.SourceSymbolId)
				&& graph.Nodes.Any(node => node.SymbolId == edge.TargetSymbolId)), Is.True);
		}

		[Test]
		public void FindCallPaths_ReportsShortestPathsAndDepthCompleteness()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			var source = workspace.SearchSymbols("CallCycleA").Single(symbol => symbol.Kind == "method");
			var target = workspace.SearchSymbols("CallCycleD").Single(symbol => symbol.Kind == "method");

			var bounded = workspace.FindCallPaths(source.SymbolId, target.SymbolId, maxDepth: 2);
			var complete = workspace.FindCallPaths(source.SymbolId, target.SymbolId, maxDepth: 3);
			var self = workspace.FindCallPaths(source.SymbolId, source.SymbolId);

			Assert.That(bounded.Found, Is.False);
			Assert.That(bounded.IsComplete, Is.False);
			Assert.That(complete.Found, Is.True);
			Assert.That(complete.Path, Has.Count.EqualTo(3));
			Assert.That(self.Found, Is.True);
			Assert.That(self.Path, Is.Empty);
		}

		[Test]
		public void CompareAssemblies_ReportsAddedAndRemovedSymbols()
		{
			string fixturePath = typeof(AnalysisFixture).Assembly.Location;
			string serverPath = typeof(AnalysisWorkspace).Assembly.Location;
			using var workspace = new AnalysisWorkspace([fixturePath, serverPath]);
			var fixture = workspace.Assemblies.Single(assembly => assembly.Name == "ICSharpCode.ILSpy.Mcp.Tests");
			var server = workspace.Assemblies.Single(assembly => assembly.Name == "ICSharpCode.ILSpy.Mcp");

			var comparison = workspace.CompareAssemblies(fixture.AssemblyId, server.AssemblyId, limit: 10);

			Assert.That(comparison.TotalChanges, Is.GreaterThan(0));
			Assert.That(comparison.Changes, Is.Not.Empty);
			Assert.That(comparison.Changes.All(change => change.ChangeKind is "added" or "removed" or "signature_changed" or "body_changed"), Is.True);
			Assert.That(comparison.IsTruncated, Is.EqualTo(comparison.TotalChanges > comparison.Changes.Count));
		}

		[Test]
		public void SymbolSearch_ReportsGenericAndOverloadedMethodsSeparately()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);

			var overloads = workspace.SearchSymbols("Overloaded").Where(symbol => symbol.Name == "Overloaded").ToArray();

			Assert.That(overloads, Has.Length.EqualTo(2));
			Assert.That(overloads.Select(symbol => symbol.DocumentationId).Distinct().Count(), Is.EqualTo(2));
			Assert.That(overloads.Any(symbol => symbol.Signature.Contains("T", System.StringComparison.Ordinal)), Is.True);
		}

		[Test]
		public void Resources_CanBePreviewedAndExportedWithoutOverwrite()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				using var manager = new WorkspaceManager([Path.GetDirectoryName(assemblyPath)!], root);
				var opened = manager.OpenWorkspace([assemblyPath]);
				string assemblyId = opened.Assemblies.Single(a => a.Name == "ICSharpCode.ILSpy.Mcp.Tests").AssemblyId;
				const string resourceName = "ICSharpCode.ILSpy.Mcp.Tests.analysis-fixture.txt";

				var data = manager.GetWorkspace(opened.WorkspaceId).ReadResource(assemblyId, resourceName);
				var exported = manager.ExportResource(opened.WorkspaceId, assemblyId, resourceName, "resources/fixture.txt");

				Assert.That(data.TextPreview, Does.Contain("resource preview fixture"));
				Assert.That(File.ReadAllText(exported.Path), Does.Contain("resource preview fixture"));
				Assert.That(() => manager.ExportResource(opened.WorkspaceId, assemblyId, resourceName, "resources/fixture.txt"),
					Throws.TypeOf<IOException>());
				Assert.That(() => manager.ExportResource(opened.WorkspaceId, assemblyId, resourceName, "../escape.txt"),
					Throws.TypeOf<UnauthorizedAccessException>());
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[Test]
		public void Resources_CanInspectResourcesFileEntriesWithoutDeserializingValues()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			using var workspace = new AnalysisWorkspace([assemblyPath]);
			string assemblyId = workspace.Assemblies.Single(a => a.Name == "ICSharpCode.ILSpy.Mcp.Tests").AssemblyId;

			var resource = workspace.ListResources(assemblyId).Single(item => item.Name.EndsWith(
				"fixture-entries.resources::Welcome", StringComparison.Ordinal));
			var preview = workspace.ReadResource(assemblyId, resource.Name);

			Assert.That(resource.Kind, Does.Contain("ResourceTypeCode.String"));
			Assert.That(preview.TextPreview, Does.Contain("Resource entries are inspected as raw data."));
		}

		[Test]
		public void PackageEntries_CanBeListedAndLoadedWithoutExtraction()
		{
			string assemblyPath = typeof(AnalysisFixture).Assembly.Location;
			string dependencyPath = typeof(AnalysisWorkspace).Assembly.Location;
			string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			string packagePath = Path.Combine(root, "fixture.zip");
			try
			{
				using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
				{
					archive.CreateEntryFromFile(assemblyPath, "managed/fixture.dll");
					archive.CreateEntryFromFile(dependencyPath, "managed/ICSharpCode.ILSpy.Mcp.dll");
				}
				using var manager = new WorkspaceManager([root], null);
				var opened = manager.OpenWorkspace([packagePath]);
				var package = manager.ListPackageEntries(opened.WorkspaceId, packagePath);
				var loaded = manager.LoadPackageEntry(opened.WorkspaceId, packagePath, "managed/fixture.dll");

				Assert.That(package.Entries, Has.Length.EqualTo(2));
				Assert.That(package.Entries.All(entry => entry.IsManagedAssembly), Is.True);
				Assert.That(loaded.Name, Is.EqualTo("ICSharpCode.ILSpy.Mcp.Tests"));
				Assert.That(loaded.Diagnostics.Any(diagnostic => diagnostic.Contains(
					"ICSharpCode.ILSpy.Mcp, Version=", System.StringComparison.Ordinal)
					&& diagnostic.Contains("could not be resolved", System.StringComparison.Ordinal)), Is.False);
				Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1));
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[Test]
		public void SearchCursor_IsBoundToQueryAndInputRevision()
		{
			string sourceAssembly = typeof(AnalysisFixture).Assembly.Location;
			string copyPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			File.Copy(sourceAssembly, copyPath);
			try
			{
				using var workspace = new AnalysisWorkspace([copyPath]);
				var firstPage = workspace.Search(string.Empty, limit: 1);
				Assert.That(firstPage.NextCursor, Is.Not.Null);
				Assert.That(() => workspace.Search("ResolveEndpoint", cursor: firstPage.NextCursor, limit: 1),
					Throws.ArgumentException.With.Message.Contains("cursor"));

				File.AppendAllText(copyPath, "changed");
				Assert.That(() => workspace.Search(string.Empty, cursor: firstPage.NextCursor, limit: 1),
					Throws.InvalidOperationException.With.Message.Contains("changed after it was loaded"));
			}
			finally
			{
				File.Delete(copyPath);
			}
		}

		[Test]
		public void InputPaths_ResolveWithinRootsAndRejectSymbolicLinkEscapes()
		{
			if (OperatingSystem.IsWindows())
				Assert.Ignore("Symbolic link creation requires additional privileges on Windows.");
			string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			string outsideAssembly = typeof(AnalysisFixture).Assembly.Location;
			try
			{
				using var manager = new WorkspaceManager([root], null);
				string localAssembly = Path.Combine(root, "fixture.dll");
				File.Copy(outsideAssembly, localAssembly);
				Assert.That(manager.OpenWorkspace(["fixture.dll"]).Assemblies, Has.Length.EqualTo(1));

				string link = Path.Combine(root, "outside");
				Directory.CreateSymbolicLink(link, Path.GetDirectoryName(outsideAssembly)!);
				Assert.That(() => manager.OpenWorkspace([Path.Combine(link, Path.GetFileName(outsideAssembly))]),
					Throws.TypeOf<UnauthorizedAccessException>());
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	public static class AnalysisFixture
	{
		public static int Counter;

		public static string ResolveEndpoint(string host) => "https://api.example.test/" + host;

		public static int AddSentinel(int value) => value + 2718;

		public static SymbolMatch ResolveInWorkspace(AnalysisWorkspace workspace) => workspace.ResolveSymbol("fixture");

		public static int Overloaded(int value) => value;

		public static T Overloaded<T>(T value) => value;

		public static string CallInterface(IFixtureContract contract) => contract.Transform("call");

		public static int ReadCounter() => Counter;

		public static void WriteCounter(int value) => Counter = value;

		public static unsafe int CallIndirect(delegate* managed<int, int> callback, int value) => callback(value);

		public static int CallCycleA(int value) => value <= 0 ? 0 : CallCycleB(value - 1);

		public static int CallCycleB(int value) => value <= 0 ? 0 : CallCycleA(value - 1) + CallCycleC(value - 1);

		public static int CallCycleC(int value) => value <= 0 ? 0 : CallCycleB(value - 1) + CallCycleD(value - 1);

		public static int CallCycleD(int value) => value;

		public static int SumLocals(int value)
		{
			int first = value + 1;
			int second = value + 2;
			int third = value + 3;
			int fourth = value + 4;
			int fifth = value + 5;
			int sixth = value + 6;
			return first + second + third + fourth + fifth + sixth;
		}

		public static int ParseOrSentinel(string value)
		{
			try
			{
				return int.Parse(value);
			}
			catch (FormatException)
			{
				return -1;
			}
		}
	}

	public interface IFixtureContract
	{
		string Transform(string value);
	}

	[AttributeUsage(AttributeTargets.Class)]
	public sealed class FixtureMarkerAttribute : Attribute
	{
	}

	[FixtureMarker]
	public sealed class MarkedFixture
	{
	}

	public sealed class FixtureContract : IFixtureContract
	{
		public string Transform(string value) => value + "!";
	}
}
