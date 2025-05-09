using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;
using Swashbuckle.AspNetCore.Swagger;

namespace SwaggerDiff.Tool.Commands;

/// <summary>
/// Stage 2: Internal command invoked via <c>dotnet exec</c> in the target app's runtime context.
/// Loads the assembly, builds the host without starting the web server, resolves ISwaggerProvider,
/// and writes a timestamped snapshot if the API surface has changed.
/// </summary>
internal sealed class SnapshotInternalCommand : Command<SnapshotInternalCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--assembly <PATH>")]
        [Description("Path to the built API assembly DLL.")]
        public string Assembly { get; set; } = string.Empty;

        [CommandOption("--output <DIR>")]
        [Description("Output directory for snapshots.")]
        public string Output { get; set; } = "Docs/Versions";

        [CommandOption("--doc-name <NAME>")]
        [Description("Swagger document name.")]
        [DefaultValue("v1")]
        public string DocName { get; set; } = "v1";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(settings.Assembly);

            var serviceProvider = HostResolver.GetServiceProvider(assembly);

            if (serviceProvider == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Could not resolve service provider from assembly.");
                return 1;
            }

            var swaggerProvider = serviceProvider.GetRequiredService<ISwaggerProvider>();
            var doc = swaggerProvider.GetSwagger(settings.DocName);

            // Serialize to JSON
            using var memoryStream = new MemoryStream();
            var jsonWriter = new Microsoft.OpenApi.Writers.OpenApiJsonWriter(
                new StreamWriter(memoryStream) { AutoFlush = true });
            doc.SerializeAsV3(jsonWriter);
            memoryStream.Position = 0;

            using var reader = new StreamReader(memoryStream);
            var json = reader.ReadToEnd();

            // Normalize for comparison
            var normalized = NormalizeSwagger(json);

            // Ensure output directory exists
            Directory.CreateDirectory(settings.Output);

            // Compare with latest snapshot
            var latestFile = Directory.GetFiles(settings.Output, "doc_*.json")
                .OrderDescending()
                .FirstOrDefault();

            if (latestFile != null)
            {
                var existingJson = File.ReadAllText(latestFile);
                var existingNormalized = NormalizeSwagger(existingJson);

                if (normalized == existingNormalized)
                {
                    AnsiConsole.MarkupLine("[green]No API changes detected.[/]");
                    return 0;
                }
            }

            // Write new snapshot
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var filename = $"doc_{timestamp}.json";
            var outputPath = Path.Combine(settings.Output, filename);

            File.WriteAllText(outputPath, json);
            AnsiConsole.MarkupLine($"[green]Snapshot saved:[/] {filename}");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            if (ex.InnerException != null)
                AnsiConsole.MarkupLine($"[red]Inner:[/] {ex.InnerException.Message.EscapeMarkup()}");
            return 1;
        }
    }

    /// <summary>
    /// Normalizes a swagger JSON document for comparison by stripping the info.version field.
    /// </summary>
    private static string NormalizeSwagger(string json)
    {
        var node = JsonNode.Parse(json);
        if (node == null) return json;

        if (node["info"] is JsonObject info)
            info["version"] = "1.0";

        return JsonSerializer.Serialize(node, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = null
        });
    }
}
