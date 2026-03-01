using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SwaggerDiff.Tool.Commands;

/// <summary>
/// Stage 1: User-facing snapshot command.
/// Resolves target assemblies (from --project, --assembly, or auto-discovery of ASP.NET Core web projects),
/// optionally builds them, then re-invokes itself via <c>dotnet exec</c> with each target app's
/// deps.json and runtimeconfig.json so that all assembly dependencies resolve correctly.
/// </summary>
internal sealed class SnapshotCommand : AsyncCommand<SnapshotCommand.Settings>
{
    /// <summary>
    /// Well-known directories that are always skipped during auto-discovery.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".idea", ".vs", "node_modules", "TestResults", "artifacts"
    };

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PATH>")]
        [Description("Path to one or more .csproj files. Repeat for multiple projects.")]
        public string[]? Project { get; set; }

        [CommandOption("--assembly <PATH>")]
        [Description("Direct path to built assembly DLL(s). Skips build and project discovery. Repeat for multiple.")]
        public string[]? Assembly { get; set; }

        [CommandOption("-c|--configuration <CONFIG>")]
        [Description("Build configuration.")]
        [DefaultValue("Debug")]
        public string Configuration { get; set; } = "Debug";

        [CommandOption("--no-build")]
        [Description("Skip the build step (assumes the project was already built).")]
        [DefaultValue(false)]
        public bool NoBuild { get; set; }

        [CommandOption("--output <DIR>")]
        [Description("Output directory for snapshots (relative to each project directory).")]
        [DefaultValue("Docs/Versions")]
        public string Output { get; set; } = "Docs/Versions";

        [CommandOption("--doc-name <NAME>")]
        [Description("Swagger document name.")]
        [DefaultValue("v1")]
        public string DocName { get; set; } = "v1";

        [CommandOption("--exclude <NAME>")]
        [Description("Project names to exclude from auto-discovery (without .csproj extension). Repeat for multiple.")]
        public string[]? Exclude { get; set; }

        [CommandOption("--exclude-dir <DIR>")]
        [Description("Directory names to exclude from auto-discovery. Repeat for multiple.")]
        public string[]? ExcludeDir { get; set; }

        [CommandOption("--parallelism <N>")]
        [Description("Maximum concurrent snapshot subprocesses. Defaults to number of projects.")]
        [DefaultValue(0)]
        public int Parallelism { get; set; }

        public override ValidationResult Validate()
        {
            if (Assembly is { Length: > 0 })
            {
                foreach (var assembly in Assembly)
                {
                    var fullPath = Path.GetFullPath(assembly);
                    if (!File.Exists(fullPath))
                        return ValidationResult.Error($"Assembly not found: {fullPath}");
                }
            }

            if (Project == null) return ValidationResult.Success();
            
            foreach (var project in Project)
            {
                var fullPath = Path.GetFullPath(project);
                if (!File.Exists(fullPath))
                    return ValidationResult.Error($"Project file not found: {fullPath}");
            }

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Multi-assembly mode — skip discovery and build entirely
        if (settings.Assembly is { Length: > 0 })
        {
            var assemblies = settings.Assembly.Select(a =>
            {
                var assemblyPath = Path.GetFullPath(a);
                var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
                var projectDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", ".."));
                var outputDir = Path.Combine(projectDir, settings.Output);
                var name = Path.GetFileNameWithoutExtension(assemblyPath);
                return (name, assemblyPath, assemblyDir, outputDir);
            }).ToList();

            AnsiConsole.MarkupLine($"[grey]Using {assemblies.Count} pre-built assembly(ies)[/]");
            return await RunSnapshotsAsync(assemblies, settings, cancellationToken);
        }

        // Resolve one or more projects
        var projects = ResolveProjects(settings);
        if (projects == null || projects.Count == 0)
            return 1;

        // ── Phase 1: Build and resolve assemblies ──
        // When --no-build is set, BuildAndResolveAssembly only evaluates MSBuild
        // properties (read-only) — safe to parallelize. Builds are always sequential
        // because shared project dependencies can cause file lock conflicts.
        List<(string name, string assemblyPath, string assemblyDir, string outputDir)> resolved;
        int buildFailures;

        if (settings.NoBuild && projects.Count > 1)
        {
            AnsiConsole.MarkupLine($"[grey]Resolving {projects.Count} assemblies in parallel (--no-build)...[/]");
            var resolvedArray = new (string projectPath, string? assemblyPath)[projects.Count];
            Parallel.For(0, projects.Count, i =>
            {
                resolvedArray[i] = (projects[i], BuildAndResolveAssembly(projects[i], settings, silent: true));
            });

            resolved = [];
            buildFailures = 0;
            foreach (var (projectPath, assemblyPath) in resolvedArray)
            {
                if (assemblyPath == null)
                {
                    buildFailures++;
                    continue;
                }

                var projectName = Path.GetFileNameWithoutExtension(projectPath);
                var projectDir = Path.GetDirectoryName(projectPath)!;
                resolved.Add((projectName, assemblyPath, Path.GetDirectoryName(assemblyPath)!, Path.Combine(projectDir, settings.Output)));
            }
        }
        else
        {
            resolved = [];
            buildFailures = 0;

            foreach (var projectPath in projects)
            {
                var projectName = Path.GetFileNameWithoutExtension(projectPath);
                var projectDir = Path.GetDirectoryName(projectPath)!;

                if (projects.Count > 1)
                    AnsiConsole.MarkupLine($"\n[bold]── {projectName.EscapeMarkup()} ──[/]");

                var assemblyPath = BuildAndResolveAssembly(projectPath, settings);
                if (assemblyPath == null)
                {
                    buildFailures++;
                    continue;
                }

                resolved.Add((projectName, assemblyPath, Path.GetDirectoryName(assemblyPath)!, Path.Combine(projectDir, settings.Output)));
            }
        }

        if (resolved.Count == 0)
            return 1;

        return await RunSnapshotsAsync(resolved, settings, cancellationToken, buildFailures);
    }

    private async Task<int> RunSnapshotsAsync(
        List<(string name, string assemblyPath, string assemblyDir, string outputDir)> resolved,
        Settings settings,
        CancellationToken cancellationToken,
        int priorFailures = 0)
    {
        var succeeded = 0;
        var failed = priorFailures;

        if (resolved.Count == 1)
        {
            var p = resolved[0];
            var exitCode = RunSnapshotSubprocess(p.assemblyPath, p.assemblyDir, p.outputDir, settings.DocName);
            if (exitCode == 0) succeeded++;
            else failed++;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            var maxParallelism = settings.Parallelism > 0 ? settings.Parallelism : resolved.Count;
            AnsiConsole.MarkupLine($"\n[grey]Generating {resolved.Count} snapshots concurrently (parallelism={maxParallelism})...[/]");

            var semaphore = new SemaphoreSlim(maxParallelism);
            var results = new SnapshotResult[resolved.Count];
            var tasks = resolved.Select((p, i) => Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    results[i] = RunSnapshotSubprocessBuffered(p.assemblyPath, p.assemblyDir, p.outputDir, settings.DocName);
                }
                catch (Exception ex)
                {
                    results[i] = new SnapshotResult(1, "", $"Error: {ex.Message}\n");
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken)).ToArray();

            await Task.WhenAll(tasks);
            sw.Stop();

            for (var i = 0; i < resolved.Count; i++)
            {
                var p = resolved[i];
                var result = results[i];

                AnsiConsole.MarkupLine($"\n[bold]── {p.name.EscapeMarkup()} ──[/]");

                if (!string.IsNullOrWhiteSpace(result.Stdout))
                    Console.Write(result.Stdout);
                if (!string.IsNullOrWhiteSpace(result.Stderr))
                    await Console.Error.WriteAsync(result.Stderr);

                if (result.ExitCode == 0) succeeded++;
                else failed++;
            }

            AnsiConsole.MarkupLine($"\n[grey]Completed in {sw.Elapsed.TotalSeconds:F1}s[/]");
        }

        var total = succeeded + failed;
        if (total > 1)
        {
            if (failed == 0)
                AnsiConsole.MarkupLine($"[green]Snapshots complete:[/] {succeeded}/{total} succeeded");
            else
                AnsiConsole.MarkupLine($"[yellow]Snapshots complete:[/] {succeeded}/{total} succeeded, {failed} failed");
        }

        return failed > 0 ? 1 : 0;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Project resolution
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the list of projects to process from explicit flags or auto-discovery.
    /// </summary>
    private static List<string>? ResolveProjects(Settings settings)
    {
        // Explicit --project flags
        if (settings.Project is { Length: > 0 })
        {
            var resolved = settings.Project.Select(Path.GetFullPath).ToList();
            AnsiConsole.MarkupLine($"[grey]Using {resolved.Count} specified project(s)[/]");
            return resolved;
        }

        // Auto-discovery: single .csproj in CWD → current behavior
        var cwd = Directory.GetCurrentDirectory();
        var localProjects = Directory.GetFiles(cwd, "*.csproj");

        if (localProjects.Length == 1)
        {
            AnsiConsole.MarkupLine($"[grey]Using project:[/] {localProjects[0]}");
            return [localProjects[0]];
        }

        // Auto-discovery: scan up to 2 levels deep for ASP.NET Core web projects
        var discovered = DiscoverWebProjects(cwd, settings.Exclude, settings.ExcludeDir);

        if (discovered.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ASP.NET Core web projects found.");
            AnsiConsole.MarkupLine("[grey]Searched for projects with Sdk=\"Microsoft.NET.Sdk.Web\" up to 2 levels deep.[/]");
            AnsiConsole.MarkupLine("[grey]Use --project to specify project paths explicitly.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[grey]Discovered {discovered.Count} web project(s):[/]");
        foreach (var p in discovered)
            AnsiConsole.MarkupLine($"[grey]  {Path.GetRelativePath(cwd, p)}[/]");

        return discovered;
    }

    /// <summary>
    /// Scans the current directory and up to 2 levels of subdirectories for ASP.NET Core web projects.
    /// Filters by <c>Sdk="Microsoft.NET.Sdk.Web"</c> and applies exclude rules.
    /// </summary>
    private static List<string> DiscoverWebProjects(string rootDir, string[]? excludeNames, string[]? excludeDirs)
    {
        var results = new List<string>();
        var excludeNameSet = new HashSet<string>(excludeNames ?? [], StringComparer.OrdinalIgnoreCase);
        var excludeDirSet = new HashSet<string>(excludeDirs ?? [], StringComparer.OrdinalIgnoreCase);

        SearchDirectory(rootDir, 0);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;

        void SearchDirectory(string dir, int depth)
        {
            if (depth > 2) return;

            // Skip well-known non-project directories
            var dirName = Path.GetFileName(dir);
            if (depth > 0 && SkippedDirectories.Contains(dirName))
                return;

            // Skip user-excluded directories
            if (depth > 0 && excludeDirSet.Contains(dirName))
                return;

            // Check .csproj files in this directory
            foreach (var csproj in Directory.GetFiles(dir, "*.csproj"))
            {
                var projectName = Path.GetFileNameWithoutExtension(csproj);

                // Skip excluded project names
                if (excludeNameSet.Contains(projectName))
                    continue;

                // Check if it's a web project
                if (IsWebProject(csproj))
                    results.Add(csproj);
            }

            // Recurse into subdirectories
            try
            {
                foreach (var subDir in Directory.GetDirectories(dir))
                    SearchDirectory(subDir, depth + 1);
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't read
            }
        }
    }

    /// <summary>
    /// Checks whether a .csproj file uses the ASP.NET Core Web SDK.
    /// </summary>
    private static bool IsWebProject(string csprojPath)
    {
        try
        {
            // Read just the first few lines — the Sdk attribute is always on line 1
            using var reader = new StreamReader(csprojPath);
            for (var i = 0; i < 3 && reader.ReadLine() is { } line; i++)
            {
                if (line.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Build and resolve
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a project (unless --no-build) and resolves its output assembly DLL path.
    /// When <paramref name="silent"/> is true, suppresses console output (used during parallel resolution).
    /// </summary>
    private static string? BuildAndResolveAssembly(string projectPath, Settings settings, bool silent = false)
    {
        if (!silent)
            AnsiConsole.MarkupLine($"[grey]Using project:[/] {projectPath}");

        if (!settings.NoBuild)
        {
            if (!silent)
                AnsiConsole.MarkupLine($"[grey]Building[/] ({settings.Configuration})...");
            var buildResult = RunProcess("dotnet",
                $"build {EscapePath(projectPath)} --configuration {settings.Configuration} --nologo -v q",
                silent);
            if (buildResult != 0)
            {
                if (!silent)
                    AnsiConsole.MarkupLine("[red]Error:[/] Build failed.");
                return null;
            }
        }

        var targetPath = GetMsBuildProperty(projectPath, "TargetPath", settings.Configuration);
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            if (!silent)
                AnsiConsole.MarkupLine("[red]Error:[/] Could not resolve assembly path from project. Try using --assembly directly.");
            return null;
        }

        return targetPath;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Subprocess execution (Stage 2)
    // ─────────────────────────────────────────────────────────────────

    private sealed record SnapshotResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Launches the Stage 2 subprocess via <c>dotnet exec</c> with the target app's dependency context.
    /// Returns buffered output without writing to the console.
    /// </summary>
    private static SnapshotResult RunSnapshotSubprocessBuffered(string assemblyPath, string assemblyDir, string outputDir, string docName)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var depsFile = Path.Combine(assemblyDir, $"{assemblyName}.deps.json");
        var runtimeConfig = Path.Combine(assemblyDir, $"{assemblyName}.runtimeconfig.json");

        if (!File.Exists(depsFile))
            return new SnapshotResult(1, "", $"Error: deps.json not found: {depsFile}\n");

        if (!File.Exists(runtimeConfig))
            return new SnapshotResult(1, "", $"Error: runtimeconfig.json not found: {runtimeConfig}\n");

        // Resolve tool paths for subprocess invocation.
        // We intentionally do NOT use --additional-deps because it causes eager resolution
        // of all tool dependencies (Spectre.Console, etc.) which fails when they aren't in
        // the target app's probing paths. Instead, Program.cs registers an AssemblyLoadContext
        // resolver that lazily loads tool dependencies from the tool's own directory.
        var toolDll = typeof(SnapshotCommand).Assembly.Location;

        var nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var args = new List<string>
        {
            "exec",
            "--depsfile", EscapePath(depsFile),
            "--runtimeconfig", EscapePath(runtimeConfig),
            "--additionalprobingpath", EscapePath(nugetPackages),
            EscapePath(toolDll),
            "_snapshot",
            "--assembly", EscapePath(assemblyPath),
            "--output", EscapePath(Path.GetFullPath(outputDir)),
            "--doc-name", docName
        };

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = assemblyDir
            }
        };

        // Signal to the target app that it's being loaded for snapshot generation.
        process.StartInfo.Environment["SWAGGERDIFF_DRYRUN"] = "true";

        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        return new SnapshotResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Launches the Stage 2 subprocess and writes output directly to the console.
    /// Used for single-project runs where buffering isn't needed.
    /// </summary>
    private static int RunSnapshotSubprocess(string assemblyPath, string assemblyDir, string outputDir, string docName)
    {
        var result = RunSnapshotSubprocessBuffered(assemblyPath, assemblyDir, outputDir, docName);

        if (!string.IsNullOrWhiteSpace(result.Stdout))
            Console.Write(result.Stdout);
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            Console.Error.Write(result.Stderr);

        return result.ExitCode;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Utilities
    // ─────────────────────────────────────────────────────────────────

    private static string? GetMsBuildProperty(string projectPath, string property, string configuration)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"msbuild {EscapePath(projectPath)} --getProperty:{property} -p:Configuration={configuration}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        return process.ExitCode == 0 ? output : null;
    }

    private static int RunProcess(string fileName, string arguments, bool silent = false)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (!silent)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
                Console.Write(stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.Error.Write(stderr);
        }

        return process.ExitCode;
    }

    private static string EscapePath(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
