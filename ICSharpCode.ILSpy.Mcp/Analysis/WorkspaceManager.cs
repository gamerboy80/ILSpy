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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ICSharpCode.ILSpy.Mcp.Analysis
{
	public sealed class WorkspaceManager : IDisposable
	{
		const int MaximumDirectoryAssemblies = 20_000;
		readonly ConcurrentDictionary<string, AnalysisWorkspace> workspaces = new(StringComparer.Ordinal);
		readonly object lifecycleLock = new();
		readonly string[] inputRoots;
		readonly string? exportRoot;
		readonly string[] referenceDirectories;
		readonly TimeSpan operationTimeout;
		readonly AnalysisLimits analysisLimits;
		volatile bool disposed;

		public WorkspaceManager(IEnumerable<string> inputRoots, string? exportRoot, IEnumerable<string>? referenceDirectories = null,
			int operationTimeoutSeconds = 30, AnalysisLimits? analysisLimits = null)
		{
			ArgumentNullException.ThrowIfNull(inputRoots);
			this.inputRoots = inputRoots.Select(CanonicalizeExistingDirectory).Distinct(PathComparer).ToArray();
			if (this.inputRoots.Length == 0)
				throw new ArgumentException("At least one allowed input root is required.", nameof(inputRoots));
			if (!string.IsNullOrWhiteSpace(exportRoot))
			{
				string configuredRoot = Path.GetFullPath(exportRoot);
				Directory.CreateDirectory(configuredRoot);
				this.exportRoot = ResolveLink(configuredRoot);
				EnsureInsideRoot(this.exportRoot, new[] { this.exportRoot }, "export root");
			}
			this.referenceDirectories = (referenceDirectories ?? Array.Empty<string>())
				.Select(CanonicalizeExistingDirectory).ToArray();
			operationTimeout = TimeSpan.FromSeconds(Math.Clamp(operationTimeoutSeconds, 1, 300));
			this.analysisLimits = analysisLimits ?? new AnalysisLimits();
		}

		public WorkspaceOpened OpenWorkspace(IEnumerable<string>? paths = null, CancellationToken cancellationToken = default)
		{
			ThrowIfDisposed();
			var selected = paths?.ToArray() ?? Array.Empty<string>();
			if (selected.Length == 0)
				selected = inputRoots;
			var files = selected.SelectMany(path => ExpandInput(path, cancellationToken)).Distinct(PathComparer).ToArray();
			var workspace = new AnalysisWorkspace(files, referenceDirectories, cancellationToken, analysisLimits);
			IReadOnlyList<AssemblyOverview> assemblies;
			IReadOnlyList<PackageInfo> packages;
			try
			{
				assemblies = workspace.GetAssemblies(cancellationToken);
				packages = workspace.GetPackages(cancellationToken);
				if (assemblies.Count == 0 && packages.Count == 0)
				{
					string details = workspace.InputDiagnostics.Count == 0 ? string.Empty
						: " Details: " + string.Join("; ", workspace.InputDiagnostics.Take(5));
					throw new InvalidOperationException("No supported managed assemblies or archives were found. Inputs may be native, AOT, or malformed." + details);
				}
			}
			catch
			{
				workspace.Dispose();
				throw;
			}
			string id = Guid.NewGuid().ToString("N");
			lock (lifecycleLock)
			{
				if (disposed || !workspaces.TryAdd(id, workspace))
				{
					workspace.Dispose();
					ThrowIfDisposed();
					throw new InvalidOperationException("A workspace with the generated identifier already exists.");
				}
			}
			return new WorkspaceOpened(id, assemblies, packages, workspace.InputDiagnostics);
		}

		public string OpenEmptyWorkspace()
		{
			ThrowIfDisposed();
			string id = Guid.NewGuid().ToString("N");
			lock (lifecycleLock)
			{
				ThrowIfDisposed();
				workspaces[id] = new AnalysisWorkspace(Array.Empty<string>(), referenceDirectories, limits: analysisLimits);
			}
			return id;
		}

		public AnalysisWorkspace GetWorkspace(string workspaceId)
		{
			lock (lifecycleLock)
			{
				ThrowIfDisposed();
				return workspaces.TryGetValue(workspaceId, out var workspace)
					? workspace : throw new KeyNotFoundException($"Workspace '{workspaceId}' is not open.");
			}
		}

		public CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
		{
			var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(operationTimeout);
			return timeout;
		}

		public AssemblyOverview LoadAssembly(string workspaceId, string path, CancellationToken cancellationToken = default)
		{
			var workspace = GetWorkspace(workspaceId);
			string fullPath = ResolveAllowedPath(path, "assembly path");
			return workspace.LoadAssemblyPath(fullPath, cancellationToken);
		}

		public PackageInfo ListPackageEntries(string workspaceId, string path, CancellationToken cancellationToken = default)
		{
			var workspace = GetWorkspace(workspaceId);
			return workspace.ListPackageEntries(ResolveAllowedPath(path, "package path"), cancellationToken);
		}

		public AssemblyOverview LoadPackageEntry(string workspaceId, string packagePath, string entryName, CancellationToken cancellationToken = default)
		{
			var workspace = GetWorkspace(workspaceId);
			return workspace.LoadPackageEntry(ResolveAllowedPath(packagePath, "package path"), entryName, cancellationToken);
		}

		public ExportResult ExportResource(string workspaceId, string assemblyId, string resourceName, string relativePath,
			CancellationToken cancellationToken = default)
		{
			string root = RequireExportRoot();
			string target = ResolveExportPath(relativePath, root);
			return GetWorkspace(workspaceId).ExportResource(assemblyId, resourceName, root,
				Path.GetRelativePath(root, target), cancellationToken);
		}

		public ExportResult ExportProject(string workspaceId, string assemblyId, string directoryName,
			CancellationToken cancellationToken = default)
		{
			string root = RequireExportRoot();
			string target = ResolveExportPath(directoryName, root);
			return GetWorkspace(workspaceId).ExportProject(assemblyId, root,
				Path.GetRelativePath(root, target), cancellationToken);
		}

		public bool CloseWorkspace(string workspaceId)
		{
			AnalysisWorkspace? workspace;
			lock (lifecycleLock)
			{
				if (!workspaces.TryRemove(workspaceId, out workspace))
					return false;
			}
			workspace.Dispose();
			return true;
		}

		IEnumerable<string> ExpandInput(string path, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string fullPath = ResolveAllowedPath(path, "input path");
			if (File.Exists(fullPath))
			{
				yield return fullPath;
				yield break;
			}
			if (!Directory.Exists(fullPath))
				throw new FileNotFoundException("The input path does not exist inside an allowed root.", fullPath);
			int candidateCount = 0;
			foreach (string candidate in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
			{
				cancellationToken.ThrowIfCancellationRequested();
				string safePath = ResolveAllowedPath(candidate, "discovered input file");
				string extension = Path.GetExtension(safePath);
				if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".apk", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".xap", StringComparison.OrdinalIgnoreCase)
					|| extension.Equals(".bundle", StringComparison.OrdinalIgnoreCase))
				{
					if (++candidateCount > MaximumDirectoryAssemblies)
						throw new InvalidOperationException($"The directory contains more than {MaximumDirectoryAssemblies} candidate files.");
					yield return safePath;
				}
			}
		}

		string ResolveAllowedPath(string path, string description)
		{
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException($"A {description} is required.", nameof(path));
			string fullPath = ResolveInputPath(path);
			string resolved = ResolveLink(fullPath);
			EnsureInsideRoot(resolved, inputRoots, description);
			return resolved;
		}

		string RequireExportRoot() => exportRoot
			?? throw new InvalidOperationException("Exporting requires an explicitly configured --export-root directory.");

		static string ResolveExportPath(string relativePath, string exportRoot)
		{
			if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
				throw new ArgumentException("An explicit relative export path is required.", nameof(relativePath));
			string fullPath = Path.GetFullPath(Path.Combine(exportRoot, relativePath));
			EnsureInsideRoot(fullPath, new[] { exportRoot }, "export path");
			var parent = Path.GetDirectoryName(fullPath);
			if (parent != null)
			{
				string existing = parent;
				while (!Directory.Exists(existing) && !File.Exists(existing) && Path.GetDirectoryName(existing) is { } next && next != existing)
					existing = next;
				string resolvedParent = ResolveLink(existing);
				EnsureInsideRoot(resolvedParent, new[] { exportRoot }, "export path");
			}
			return fullPath;
		}

		static string CanonicalizeExistingDirectory(string path)
		{
			string fullPath = Path.GetFullPath(path);
			if (!Directory.Exists(fullPath))
				throw new DirectoryNotFoundException($"Allowed input root '{fullPath}' does not exist.");
			return ResolveLink(fullPath);
		}

		static string ResolveLink(string path)
		{
			string fullPath = Path.GetFullPath(path);
			string root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("The path has no filesystem root.", nameof(path));
			string current = root;
			foreach (string segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
				StringSplitOptions.RemoveEmptyEntries))
			{
				current = Path.Combine(current, segment);
				FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
				try
				{
					current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
				}
				catch (IOException) when (!info.Exists)
				{
				}
			}
			return Path.GetFullPath(current);
		}

		string ResolveInputPath(string path)
		{
			if (Path.IsPathRooted(path))
				return Path.GetFullPath(path);
			foreach (string root in inputRoots)
			{
				string candidate = Path.GetFullPath(Path.Combine(root, path));
				if (File.Exists(candidate) || Directory.Exists(candidate))
					return candidate;
			}
			return Path.GetFullPath(path);
		}

		static void EnsureInsideRoot(string path, IEnumerable<string> roots, string description)
		{
			string fullPath = Path.GetFullPath(path);
			foreach (string root in roots)
			{
				string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (fullPath.Equals(normalizedRoot, PathComparison)
					|| fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison))
					return;
			}
			throw new UnauthorizedAccessException($"The {description} must be inside a configured root.");
		}

		static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

		void ThrowIfDisposed()
		{
			if (disposed)
				throw new ObjectDisposedException(nameof(WorkspaceManager));
		}

		public void Dispose()
		{
			AnalysisWorkspace[] snapshot;
			lock (lifecycleLock)
			{
				if (disposed)
					return;
				disposed = true;
				snapshot = workspaces.Values.ToArray();
				workspaces.Clear();
			}
			foreach (var workspace in snapshot)
				workspace.Dispose();
		}
	}

	public sealed record WorkspaceOpened(string WorkspaceId, IReadOnlyList<AssemblyOverview> Assemblies,
		IReadOnlyList<PackageInfo> Packages, IReadOnlyList<string> Diagnostics);
}
