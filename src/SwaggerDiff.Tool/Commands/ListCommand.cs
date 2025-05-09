using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SwaggerDiff.Tool.Commands;

internal sealed class ListCommand : Command<ListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--dir <PATH>")]
        [Description("Directory to scan for snapshot files.")]
        [DefaultValue("Docs/Versions")]
        public string Dir { get; set; } = "Docs/Versions";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var dir = Path.GetFullPath(settings.Dir);

        if (!Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine($"[yellow]Directory not found:[/] {dir.EscapeMarkup()}");
            return 0;
        }

        var files = Directory.GetFiles(dir, "doc_*.json").OrderDescending().ToArray();

        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No snapshots found.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Snapshot")
            .AddColumn("Size")
            .AddColumn("Created (UTC)");

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            table.AddRow(
                Path.GetFileNameWithoutExtension(file),
                FormatSize(info.Length),
                info.CreationTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[bold]{files.Length}[/] snapshot(s) found.");

        return 0;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
