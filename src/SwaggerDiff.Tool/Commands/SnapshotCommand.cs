using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SwaggerDiff.Tool.Commands;

/// <summary>
/// Stage 1: User-facing snapshot command.
/// Resolves the target assembly (from --project, --assembly, or auto-discovery),
/// optionally builds it, then re-invokes itself via <c>dotnet exec</c> with the target app's
/// deps.json and runtimeconfig.json so that all assembly dependencies resolve correctly.
/// </summary>
internal sealed class SnapshotCommand : Command<SnapshotCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PATH>")]
        [Description("Path to the .csproj file. Defaults to the single .csproj in the current directory.")]
        public string? Project { get; set; }

        [CommandOption("--assembly <PATH>")]
        [Description("Direct path to a built assembly DLL. Overrides --project (skips build).")]
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
        [Description("Output directory for snapshots.")]
        [DefaultValue("Docs/Versions")]
        public string Output { get; set; } = "Docs/Versions";

        [CommandOption("--doc-name <NAME>")]
        [Description("Swagger document name.")]
        [DefaultValue("v1")]
        public string DocName { get; set; } = "v1";

        public override ValidationResult Validate()
        {
            if (!string.IsNullOrWhiteSpace(Assembly))
            {
                var fullPath = Path.GetFullPath(Assembly);
                if (!File.Exists(fullPath))
                    return ValidationResult.Error($"Assembly not found: {fullPath}");
            }

            if (!string.IsNullOrWhiteSpace(Project))
            {
                var fullPath = Path.GetFullPath(Project);
                if (!File.Exists(fullPath))
                    return ValidationResult.Error($"Project file not found: {fullPath}");
            }

            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // 1. Resolve the assembly path
        var assemblyPath = ResolveAssemblyPath(settings);
        if (assemblyPath == null)
            return 1;

        var outputDir = Path.GetFullPath(settings.Output);
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
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

        // 2. Re-invoke as: dotnet exec --depsfile ... --runtimeconfig ... <tool>.dll _snapshot ...
        var toolDll = typeof(SnapshotCommand).Assembly.Location;

        var processArgs = string.Join(" ",
        [
            "exec",
            "--depsfile", EscapePath(depsFile),
            "--runtimeconfig", EscapePath(runtimeConfig),
            EscapePath(toolDll),
            "_snapshot",
            "--assembly", EscapePath(assemblyPath),
            "--output", EscapePath(outputDir),
            "--doc-name", settings.DocName
        ]);

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

    /// <summary>
    /// Resolves the assembly DLL path from --assembly, --project, or auto-discovery.
    /// Builds the project first unless --no-build or --assembly is specified.
    /// </summary>
    private static string? ResolveAssemblyPath(Settings settings)
    {
        // Direct assembly path — skip everything
        if (!string.IsNullOrWhiteSpace(settings.Assembly))
        {
            AnsiConsole.MarkupLine($"[grey]Using assembly:[/] {settings.Assembly}");
            return Path.GetFullPath(settings.Assembly);
        }

        // Resolve the .csproj path
        var projectPath = ResolveProjectPath(settings.Project);
        if (projectPath == null)
            return null;

        AnsiConsole.MarkupLine($"[grey]Using project:[/] {projectPath}");

        // Build unless --no-build
        if (!settings.NoBuild)
        {
            AnsiConsole.MarkupLine($"[grey]Building[/] ({settings.Configuration})...");
            var buildResult = RunProcess("dotnet", $"build {EscapePath(projectPath)} --configuration {settings.Configuration} --nologo -v q");
            if (buildResult != 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Build failed.");
                return null;
            }
        }

        // Resolve the output DLL path via MSBuild
        var targetPath = GetMsBuildProperty(projectPath, "TargetPath", settings.Configuration);
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Could not resolve assembly path from project. Try using --assembly directly.");
            return null;
        }

        return targetPath;
    }

    /// <summary>
    /// Resolves the .csproj file path from an explicit --project value or auto-discovers
    /// the single .csproj in the current directory.
    /// </summary>
    private static string? ResolveProjectPath(string? explicitProject)
    {
        if (!string.IsNullOrWhiteSpace(explicitProject))
            return Path.GetFullPath(explicitProject);

        // Auto-discover: find .csproj files in the current directory
        var csprojFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");

        return csprojFiles.Length switch
        {
            0 => Error("No .csproj file found in the current directory. Use --project or --assembly."),
            1 => csprojFiles[0],
            _ => Error($"Multiple .csproj files found. Use --project to specify which one:\n"
                       + string.Join("\n", csprojFiles.Select(f => $"  {Path.GetFileName(f)}")))
        };
    }

    /// <summary>
    /// Reads an MSBuild property from a project file using <c>dotnet msbuild --getProperty</c>.
    /// </summary>
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

    private static string? Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {message.EscapeMarkup()}");
        return null;
    }

    private static string EscapePath(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
