using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SwaggerDiff.Tool.Commands;

/// <summary>
/// Stage 1: User-facing snapshot command.
/// Validates inputs, then re-invokes itself via <c>dotnet exec</c> with the target app's
/// deps.json and runtimeconfig.json so that all assembly dependencies resolve correctly.
/// </summary>
internal sealed class SnapshotCommand : Command<SnapshotCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--assembly <PATH>")]
        [Description("Path to the built API assembly DLL.")]
        public string Assembly { get; set; } = string.Empty;

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
            if (string.IsNullOrWhiteSpace(Assembly))
                return ValidationResult.Error("--assembly is required.");

            var fullPath = Path.GetFullPath(Assembly);
            if (!File.Exists(fullPath))
                return ValidationResult.Error($"Assembly not found: {fullPath}");

            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var assemblyPath = Path.GetFullPath(settings.Assembly);
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

        // Re-invoke as: dotnet exec --depsfile <app>.deps.json --runtimeconfig <app>.runtimeconfig.json <tool>.dll _snapshot ...
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

    private static string EscapePath(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
