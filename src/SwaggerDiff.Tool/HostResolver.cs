using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace SwaggerDiff.Tool;

/// <summary>
/// Resolves an <see cref="IServiceProvider"/> from an assembly's entry point
/// using the same HostFactoryResolver pattern as Swashbuckle CLI and dotnet-ef.
/// The host is started with a <see cref="NoOpServer"/> so no ports are bound.
/// </summary>
internal static class HostResolver
{
    public static IServiceProvider? GetServiceProvider(Assembly assembly)
    {
        // Use HostFactoryResolver pattern via diagnostic listener — works for .NET 6+ minimal APIs
        var hostFactoryResolver = typeof(IHost).Assembly
            .GetType("Microsoft.Extensions.Hosting.HostFactoryResolver");

        if (hostFactoryResolver == null)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] HostFactoryResolver not found. Trying fallback...");
            return GetServiceProviderFallback(assembly);
        }

        var resolveMethod = hostFactoryResolver.GetMethod(
            "ResolveHostFactory",
            BindingFlags.Public | BindingFlags.Static);

        if (resolveMethod == null)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] ResolveHostFactory method not found. Trying fallback...");
            return GetServiceProviderFallback(assembly);
        }

        Action<object> configureHostBuilder = hostBuilder =>
        {
            if (hostBuilder is IHostBuilder builder)
            {
                builder.ConfigureServices((_, services) =>
                {
                    // Replace the real server with a no-op to avoid binding ports
                    services.AddSingleton<IServer, NoOpServer>();

                    // Remove all IHostedService registrations except GenericWebHostService
                    // (which is required for the middleware pipeline / DI to be fully configured)
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

        Action<Exception?> entrypointCompleted = _ => { };

        // ResolveHostFactory(assembly, waitTimeout, stopApplication, configureHostBuilder, entrypointCompleted)
        var factory = resolveMethod.Invoke(null, new object?[]
        {
            assembly,
            TimeSpan.FromSeconds(30),
            false, // stopApplication = false — we need the host to start for full DI
            configureHostBuilder,
            entrypointCompleted
        }) as Func<string[], object>;

        if (factory == null)
            return GetServiceProviderFallback(assembly);

        var assemblyName = assembly.GetName()?.FullName ?? assembly.GetName()?.Name;
        var host = factory([$"--applicationName={assemblyName}"]) as IHost;

        if (host == null)
            return GetServiceProviderFallback(assembly);

        // Wait for ApplicationStarted to ensure services are fully initialized
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var tcs = new TaskCompletionSource<object?>();
        using var reg = lifetime.ApplicationStarted.Register(() => tcs.TrySetResult(null));

        // Start the host (with NoOpServer, this won't bind any ports)
        host.StartAsync().GetAwaiter().GetResult();

        if (!tcs.Task.Wait(TimeSpan.FromSeconds(30)))
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Timed out waiting for application to start.");
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
                    builder.UseServer(new NoOpServer());
                })
                .Build()
                .Services;
        }
        catch
        {
            return null;
        }
    }
}
