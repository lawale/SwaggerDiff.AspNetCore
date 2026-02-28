using Spectre.Console.Cli;
using SwaggerDiff.Tool.Commands;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("swaggerdiff");

    config.AddCommand<SnapshotCommand>("snapshot")
        .WithDescription("Generate OpenAPI snapshots from built assemblies. Auto-discovers ASP.NET Core web projects when run from a solution directory.")
        .WithExample("snapshot")
        .WithExample("snapshot", "--project", "./src/MyApi/MyApi.csproj")
        .WithExample("snapshot", "--project", "./src/Api1/Api1.csproj", "--project", "./src/Api2/Api2.csproj")
        .WithExample("snapshot", "--exclude", "MyApi.Tests", "--exclude-dir", "tests")
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
