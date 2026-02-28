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
internal sealed class SnapshotCommand : Command<SnapshotCommand.Settings>
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
        [Description("Direct path to a built assembly DLL. Overrides --project (skips build). Single project only.")]
        public string? Assembly { get; set; }

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

        public override ValidationResult Validate()
        {
            if (!string.IsNullOrWhiteSpace(Assembly))
            {
                var fullPath = Path.GetFullPath(Assembly);
                if (!File.Exists(fullPath))
                    return ValidationResult.Error($"Assembly not found: {fullPath}");
            }

            if (Project != null)
            {
                foreach (var project in Project)
                {
                    var fullPath = Path.GetFullPath(project);
                    if (!File.Exists(fullPath))
                        return ValidationResult.Error($"Project file not found: {fullPath}");
                }
            }

            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Single assembly mode — unchanged behavior
        if (!string.IsNullOrWhiteSpace(settings.Assembly))
        {
            AnsiConsole.MarkupLine($"[grey]Using assembly:[/] {settings.Assembly}");
            var assemblyPath = Path.GetFullPath(settings.Assembly);
            var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
            var outputDir = Path.GetFullPath(settings.Output);
            return RunSnapshotSubprocess(assemblyPath, assemblyDir, outputDir, settings.DocName);
        }

        // Resolve one or more projects
        var projects = ResolveProjects(settings);
        if (projects == null || projects.Count == 0)
            return 1;

        var succeeded = 0;
        var failed = 0;

        foreach (var projectPath in projects)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectDir = Path.GetDirectoryName(projectPath)!;

            if (projects.Count > 1)
                AnsiConsole.MarkupLine($"\n[bold]── {projectName.EscapeMarkup()} ──[/]");

            // Build and resolve the assembly DLL
            var assemblyPath = BuildAndResolveAssembly(projectPath, settings);
            if (assemblyPath == null)
            {
                failed++;
                continue;
            }

            // Output directory is relative to the project's directory
            var outputDir = Path.Combine(projectDir, settings.Output);
            var assemblyDir = Path.GetDirectoryName(assemblyPath)!;

            var exitCode = RunSnapshotSubprocess(assemblyPath, assemblyDir, outputDir, settings.DocName);
            if (exitCode == 0)
                succeeded++;
            else
                failed++;
        }

        // Print summary for multi-project runs
        if (projects.Count > 1)
        {
            AnsiConsole.WriteLine();
            if (failed == 0)
                AnsiConsole.MarkupLine($"[green]Snapshots complete:[/] {succeeded}/{projects.Count} succeeded");
            else
                AnsiConsole.MarkupLine($"[yellow]Snapshots complete:[/] {succeeded}/{projects.Count} succeeded, {failed} failed");
        }

        return failed > 0 ? 1 : 0;
    }
    
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

    /// <summary>
    /// Builds a project (unless --no-build) and resolves its output assembly DLL path.
    /// </summary>
    private static string? BuildAndResolveAssembly(string projectPath, Settings settings)
    {
        AnsiConsole.MarkupLine($"[grey]Using project:[/] {projectPath}");

        if (!settings.NoBuild)
        {
            AnsiConsole.MarkupLine($"[grey]Building[/] ({settings.Configuration})...");
            var buildResult = RunProcess("dotnet",
                $"build {EscapePath(projectPath)} --configuration {settings.Configuration} --nologo -v q");
            if (buildResult != 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Build failed.");
                return null;
            }
        }

        var targetPath = GetMsBuildProperty(projectPath, "TargetPath", settings.Configuration);
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Could not resolve assembly path from project. Try using --assembly directly.");
            return null;
        }

        return targetPath;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Subprocess execution (Stage 2)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the Stage 2 subprocess via <c>dotnet exec</c> with the target app's dependency context.
    /// </summary>
    private static int RunSnapshotSubprocess(string assemblyPath, string assemblyDir, string outputDir, string docName)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var depsFile = Path.Combine(assemblyDir, $"{assemblyName}.deps.json");
        var runtimeConfig = Path.Combine(assemblyDir, $"{assemblyName}.runtimeconfig.json");

        if (!File.Exists(depsFile))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] deps.json not found: {depsFile.EscapeMarkup()}");
            return 1;
        }

        if (!File.Exists(runtimeConfig))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] runtimeconfig.json not found: {runtimeConfig.EscapeMarkup()}");
            return 1;
        }

        // Resolve tool paths for --additional-deps and --additionalprobingpath
        var toolDll = typeof(SnapshotCommand).Assembly.Location;
        var toolDir = Path.GetDirectoryName(toolDll)!;
        var toolDepsFile = Path.Combine(toolDir,
            Path.GetFileNameWithoutExtension(toolDll) + ".deps.json");

        var nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var args = new List<string>
        {
            "exec",
            "--depsfile", EscapePath(depsFile),
            "--runtimeconfig", EscapePath(runtimeConfig)
        };

        if (File.Exists(toolDepsFile))
            args.AddRange(["--additional-deps", EscapePath(toolDepsFile)]);

        args.AddRange(["--additionalprobingpath", EscapePath(nugetPackages)]);
        args.AddRange(["--additionalprobingpath", EscapePath(toolDir)]);

        args.AddRange(
        [
            EscapePath(toolDll),
            "_snapshot",
            "--assembly", EscapePath(assemblyPath),
            "--output", EscapePath(Path.GetFullPath(outputDir)),
            "--doc-name", docName
        ]);

        var processArgs = string.Join(" ", args);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = processArgs,
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

        if (!string.IsNullOrWhiteSpace(stdout))
            Console.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.Write(stderr);

        return process.ExitCode;
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

    private static int RunProcess(string fileName, string arguments)
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

        if (!string.IsNullOrWhiteSpace(stdout))
            Console.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.Write(stderr);

        return process.ExitCode;
    }

    private static string EscapePath(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
