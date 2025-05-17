using Spectre.Console.Cli;
using SwaggerDiff.Tool.Commands;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("swagger-diff");

    config.AddCommand<SnapshotCommand>("snapshot")
        .WithDescription("Generate a new OpenAPI snapshot from a built assembly.")
        .WithExample("snapshot")
        .WithExample("snapshot", "--project", "./src/MyApi/MyApi.csproj")
        .WithExample("snapshot", "-c", "Release", "--output", "Docs/Versions")
        .WithExample("snapshot", "--assembly", "./bin/Release/net8.0/MyApi.dll");

    config.AddCommand<ListCommand>("list")
        .WithDescription("List available OpenAPI snapshots in a directory.")
        .WithExample("list")
        .WithExample("list", "--dir", "Docs/Versions");

    // Internal command used by the two-stage subprocess pattern — hidden from help
    config.AddCommand<SnapshotInternalCommand>("_snapshot")
        .IsHidden();
});

return app.Run(args);
