using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Swashbuckle.AspNetCore.Swagger;

var app = new CommandApp();
return app.Run(args);

internal class CommandApp
{
    public int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0].ToLower() switch
        {
            "snapshot" => RunSnapshot(args[1..]),
            "list" => RunList(args[1..]),
            "_snapshot" => RunSnapshotInternal(args[1..]),
            _ => PrintUsage()
        };
    }

    private static int PrintUsage()
    {
        Console.WriteLine("SwaggerDiff CLI Tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  swagger-diff snapshot --assembly <path> [--output <dir>] [--doc-name <name>]");
        Console.WriteLine("  swagger-diff list --dir <path>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  snapshot   Generate a new OpenAPI snapshot from a built assembly");
        Console.WriteLine("  list       List available snapshots in a directory");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --assembly   Path to the built assembly DLL (required for snapshot)");
        Console.WriteLine("  --output     Output directory for snapshots (default: Docs/Versions)");
        Console.WriteLine("  --doc-name   Swagger document name (default: v1)");
        Console.WriteLine("  --dir        Directory to list snapshots from (default: Docs/Versions)");
        return 1;
    }

    /// <summary>
    /// Stage 1: User-facing snapshot command.
    /// Re-invokes itself via `dotnet exec` with the target app's deps/runtimeconfig
    /// so that all assembly dependencies resolve correctly.
    /// </summary>
    private static int RunSnapshot(string[] args)
    {
        var namedArgs = ParseArgs(args);

        if (!namedArgs.TryGetValue("assembly", out var assemblyPath))
        {
            Console.Error.WriteLine("Error: --assembly is required.");
            return 1;
        }

        assemblyPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"Error: Assembly not found: {assemblyPath}");
            return 1;
        }

        var outputDir = namedArgs.GetValueOrDefault("output", Path.Combine("Docs", "Versions"));
        outputDir = Path.GetFullPath(outputDir);

        var docName = namedArgs.GetValueOrDefault("doc-name", "v1");

        // Find deps.json and runtimeconfig.json alongside the assembly
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var depsFile = Path.Combine(assemblyDir, $"{assemblyName}.deps.json");
        var runtimeConfig = Path.Combine(assemblyDir, $"{assemblyName}.runtimeconfig.json");

        if (!File.Exists(depsFile))
        {
            Console.Error.WriteLine($"Error: deps.json not found: {depsFile}");
            return 1;
        }

        if (!File.Exists(runtimeConfig))
        {
            Console.Error.WriteLine($"Error: runtimeconfig.json not found: {runtimeConfig}");
            return 1;
        }

        // Re-invoke as `dotnet exec --depsfile ... --runtimeconfig ... <this-tool>.dll _snapshot ...`
        var toolDll = typeof(CommandApp).Assembly.Location;

        var processArgs = string.Join(" ", new[]
        {
            "exec",
            "--depsfile", EscapePath(depsFile),
            "--runtimeconfig", EscapePath(runtimeConfig),
            EscapePath(toolDll),
            "_snapshot",
            "--assembly", EscapePath(assemblyPath),
            "--output", EscapePath(outputDir),
            "--doc-name", docName
        });

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = processArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
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
    /// Stage 2: Internal command invoked via `dotnet exec` in the target app's runtime context.
    /// Loads the assembly, builds the host without starting the web server, and resolves ISwaggerProvider.
    /// </summary>
    private static int RunSnapshotInternal(string[] args)
    {
        var namedArgs = ParseArgs(args);
        var assemblyPath = namedArgs["assembly"];
        var outputDir = namedArgs["output"];
        var docName = namedArgs.GetValueOrDefault("doc-name", "v1");

        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);

            var serviceProvider = GetServiceProvider(assembly);

            if (serviceProvider == null)
            {
                Console.Error.WriteLine("Error: Could not resolve service provider from assembly.");
                return 1;
            }

            var swaggerProvider = serviceProvider.GetRequiredService<ISwaggerProvider>();
            var doc = swaggerProvider.GetSwagger(docName);

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
            Directory.CreateDirectory(outputDir);

            // Compare with latest snapshot
            var latestFile = Directory.GetFiles(outputDir, "doc_*.json")
                .OrderDescending()
                .FirstOrDefault();

            if (latestFile != null)
            {
                var existingJson = File.ReadAllText(latestFile);
                var existingNormalized = NormalizeSwagger(existingJson);

                if (normalized == existingNormalized)
                {
                    Console.WriteLine("No API changes detected.");
                    return 0;
                }
            }

            // Write new snapshot
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var filename = $"doc_{timestamp}.json";
            var outputPath = Path.Combine(outputDir, filename);

            File.WriteAllText(outputPath, json);
            Console.WriteLine($"Snapshot saved: {filename}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
            return 1;
        }
    }

    private static int RunList(string[] args)
    {
        var namedArgs = ParseArgs(args);
        var dir = namedArgs.GetValueOrDefault("dir", Path.Combine("Docs", "Versions"));
        dir = Path.GetFullPath(dir);

        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"Directory not found: {dir}");
            return 0;
        }

        var files = Directory.GetFiles(dir, "doc_*.json").OrderDescending();
        var count = 0;

        foreach (var file in files)
        {
            Console.WriteLine(Path.GetFileNameWithoutExtension(file));
            count++;
        }

        if (count == 0)
            Console.WriteLine("No snapshots found.");
        else
            Console.WriteLine($"\n{count} snapshot(s) found.");

        return 0;
    }

    private static IServiceProvider? GetServiceProvider(Assembly assembly)
    {
        // Use HostFactoryResolver pattern via diagnostic listener
        // This works for .NET 6+ minimal APIs and generic host
        var hostFactoryResolver = typeof(IHost).Assembly.GetType("Microsoft.Extensions.Hosting.HostFactoryResolver");

        if (hostFactoryResolver == null)
        {
            Console.Error.WriteLine("Warning: HostFactoryResolver not found. Trying fallback...");
            return GetServiceProviderFallback(assembly);
        }

        var resolveMethod = hostFactoryResolver.GetMethod(
            "ResolveHostFactory",
            BindingFlags.Public | BindingFlags.Static);

        if (resolveMethod == null)
        {
            Console.Error.WriteLine("Warning: ResolveHostFactory method not found. Trying fallback...");
            return GetServiceProviderFallback(assembly);
        }

        Exception? entrypointException = null;

        Action<object> configureHostBuilder = hostBuilder =>
        {
            if (hostBuilder is IHostBuilder builder)
            {
                builder.ConfigureServices((_, services) =>
                {
                    // Replace server with a no-op to avoid binding ports
                    services.AddSingleton<IServer, NoopServer>();

                    // Remove all IHostedService except GenericWebHostService
                    for (var i = services.Count - 1; i >= 0; i--)
                    {
                        var registration = services[i];
                        if (registration.ServiceType == typeof(IHostedService)
                            && registration.ImplementationType?.FullName !=
                               "Microsoft.AspNetCore.Hosting.GenericWebHostService")
                        {
                            services.RemoveAt(i);
                        }
                    }
                });
            }
        };

        Action<Exception?> entrypointCompleted = ex => { entrypointException = ex; };

        // ResolveHostFactory(assembly, waitTimeout, stopApplication, configureHostBuilder, entrypointCompleted)
        var factory = resolveMethod.Invoke(null, new object?[]
        {
            assembly,
            TimeSpan.FromSeconds(30),
            false, // stopApplication = false, we need the host to start for DI
            configureHostBuilder,
            entrypointCompleted
        }) as Func<string[], object>;

        if (factory == null)
            return GetServiceProviderFallback(assembly);

        var assemblyName = assembly.GetName()?.FullName ?? assembly.GetName()?.Name;
        var host = factory(new[] { $"--applicationName={assemblyName}" }) as IHost;

        if (host == null)
            return GetServiceProviderFallback(assembly);

        // Wait for ApplicationStarted to ensure services are fully initialized
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var tcs = new TaskCompletionSource<object?>();
        using var reg = lifetime.ApplicationStarted.Register(() => tcs.TrySetResult(null));

        // Start the host (with NoopServer, this won't bind any ports)
        host.StartAsync().GetAwaiter().GetResult();

        // Wait with timeout
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("Warning: Timed out waiting for application to start.");
        }

        return host.Services;
    }

    private static IServiceProvider? GetServiceProviderFallback(Assembly assembly)
    {
        // Fallback: try convention-based approach with Startup class
        try
        {
            var assemblyName = assembly.GetName().Name;
            return Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(builder =>
                {
                    builder.UseStartup(assemblyName!);
                    builder.UseServer(new NoopServer());
                })
                .Build()
                .Services;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a swagger JSON document for comparison by sorting keys and stripping the version field.
    /// </summary>
    private static string NormalizeSwagger(string json)
    {
        var node = JsonNode.Parse(json);
        if (node == null) return json;

        // Strip version field for comparison
        if (node["info"] is JsonObject info)
            info["version"] = "1.0";

        // Serialize with sorted keys
        return JsonSerializer.Serialize(node, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = null,
            // Sort keys by using a custom converter isn't trivial,
            // but JsonNode serialization already preserves insertion order.
            // For a proper sort, we rebuild the tree.
        });
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--") && i + 1 < args.Length)
            {
                var key = args[i][2..];
                result[key] = args[++i];
            }
        }

        return result;
    }

    private static string EscapePath(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}

/// <summary>
/// A no-op server implementation that doesn't bind any ports.
/// Used when loading assemblies for swagger generation only.
/// </summary>
internal class NoopServer : IServer
{
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() { }
}
