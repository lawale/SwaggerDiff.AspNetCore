using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Spectre.Console.Cli;
using SwaggerDiff.Tool.Commands;

namespace SwaggerDiff.Tool;

public static class Program
{
    public static int Main(string[] args)
    {
        // This is critical for the Stage 2 subprocess pattern: when the tool DLL is loaded via
        // `dotnet exec` with the target app's deps.json, the runtime won't know about tool-specific
        // dependencies. The resolver loads them from the tool's own directory.
        RegisterToolAssemblyResolver();
        return RunApp(args);
    }

    private static void RegisterToolAssemblyResolver()
    {
        var toolDir = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            var candidatePath = Path.Combine(toolDir, assemblyName.Name + ".dll");
            return File.Exists(candidatePath) ? context.LoadFromAssemblyPath(candidatePath) : null;
        };
    }

    // NoInlining prevents the JIT from inlining this into Main, which would cause it to
    // try to resolve Spectre.Console types before the assembly resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(string[] args)
    {
        var app = new CommandApp();

        app.Configure(config =>
        {
            config.SetApplicationName("swaggerdiff");

            config.AddCommand<SnapshotCommand>("snapshot")
                .WithDescription(
                    "Generate OpenAPI snapshots from built assemblies. Auto-discovers ASP.NET Core web projects when run from a solution directory.")
                .WithExample("snapshot")
                .WithExample("snapshot", "--project", "./src/MyApi/MyApi.csproj")
                .WithExample("snapshot", "--project", "./src/Api1/Api1.csproj", "--project",
                    "./src/Api2/Api2.csproj")
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
    }
}
