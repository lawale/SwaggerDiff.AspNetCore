using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace SwaggerDiff.Tool;

/// <summary>
/// Resolves an <see cref="IServiceProvider"/> from an assembly's entry point by subscribing
/// to <see cref="DiagnosticListener"/> events emitted by the hosting infrastructure.
/// <para>
/// When <c>WebApplication.CreateBuilder().Build()</c> (or <c>HostBuilder.Build()</c>) executes,
/// the framework publishes <c>HostBuilding</c> and <c>HostBuilt</c> events on a
/// <c>"Microsoft.Extensions.Hosting"</c> diagnostic source. We intercept these to:
/// <list type="number">
///   <item>Replace the real server with <see cref="NoOpServer"/> so no ports are bound.</item>
///   <item>Remove background <see cref="IHostedService"/> registrations to avoid side-effects.</item>
///   <item>Capture the fully-built <see cref="IHost"/> and return its service provider.</item>
/// </list>
/// </para>
/// This approach avoids depending on the internal <c>HostFactoryResolver</c> class, which is
/// not reliably accessible via reflection across different runtime/SDK versions and
/// <c>dotnet exec</c> dependency contexts.
/// </summary>
internal static class HostResolver
{
    public static IServiceProvider? GetServiceProvider(Assembly assembly)
    {
        var result = ResolveViaDiagnosticListener(assembly);
        if (result != null)
            return result;

        // Fallback: try convention-based approach with Startup class (pre-.NET 6 apps)
        AnsiConsole.MarkupLine("[yellow]Warning:[/] DiagnosticListener approach did not resolve host. Trying Startup-based fallback...");
        return GetServiceProviderFallback(assembly);
    }

    private static IServiceProvider? ResolveViaDiagnosticListener(Assembly assembly)
    {
        var entryPoint = assembly.EntryPoint;
        if (entryPoint == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Assembly has no entry point.");
            return null;
        }

        IHost? capturedHost = null;
        var hostBuiltSignal = new ManualResetEventSlim(false);
        IDisposable? hostingSubscription = null;

        // Subscribe to all DiagnosticListeners; when the hosting listener appears, subscribe to its events
        var outerSubscription = DiagnosticListener.AllListeners.Subscribe(
            new DelegateObserver<DiagnosticListener>(listener =>
            {
                if (listener.Name == "Microsoft.Extensions.Hosting")
                {
                    hostingSubscription = listener.Subscribe(
                        new DelegateObserver<KeyValuePair<string, object?>>(kvp =>
                        {
                            switch (kvp.Key)
                            {
                                case "HostBuilding":
                                    // Inject NoOpServer and strip hosted services before Build() completes
                                    ConfigureHostBuilder(kvp.Value!);
                                    break;

                                case "HostBuilt" when kvp.Value is IHost host:
                                    capturedHost = host;
                                    hostBuiltSignal.Set();
                                    break;
                            }
                        }));
                }
            }));

        // Run the target app's entry point on a background thread.
        // app.Run() will block that thread, but since it's a background thread it won't
        // prevent this process from exiting.
        Exception? entryPointException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var parameters = entryPoint.GetParameters();
                var assemblyName = assembly.GetName()?.FullName ?? assembly.GetName()?.Name;
                object?[] args = parameters.Length == 0
                    ? Array.Empty<object>()
                    : [new[] { $"--applicationName={assemblyName}" }];
                entryPoint.Invoke(null, args);
            }
            catch (TargetInvocationException tie)
            {
                // HostAbortedException / OperationCanceledException are expected when the host shuts down
                if (tie.InnerException is not OperationCanceledException)
                {
                    entryPointException = tie.InnerException ?? tie;
                    hostBuiltSignal.Set();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entryPointException = ex;
                hostBuiltSignal.Set();
            }
        }) { IsBackground = true };
        thread.Start();

        // Wait for the HostBuilt event (or an error / timeout)
        if (!hostBuiltSignal.Wait(TimeSpan.FromSeconds(30)))
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Timed out waiting for application host to build (30 s).");
            outerSubscription.Dispose();
            hostingSubscription?.Dispose();
            return null;
        }

        outerSubscription.Dispose();
        hostingSubscription?.Dispose();

        if (capturedHost == null)
        {
            if (entryPointException != null)
                AnsiConsole.MarkupLine($"[red]Error:[/] Entry point failed: {entryPointException.Message.EscapeMarkup()}");
            return null;
        }

        // The entry point thread will call app.Run() → host.StartAsync().
        // Wait for ApplicationStarted so services that initialise during startup are ready.
        try
        {
            var lifetime = capturedHost.Services.GetRequiredService<IHostApplicationLifetime>();
            var started = new ManualResetEventSlim(false);
            using var reg = lifetime.ApplicationStarted.Register(() => started.Set());

            if (!started.IsSet)
                started.Wait(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // If we can't wait for startup, the DI container should still be usable
        }

        return capturedHost.Services;
    }

    /// <summary>
    /// Configures the host builder to use <see cref="NoOpServer"/> and removes background services.
    /// Handles both <c>IHostBuilder</c> (legacy) and <c>HostApplicationBuilder</c> (.NET 7+ minimal hosting).
    /// </summary>
    private static void ConfigureHostBuilder(object hostBuilder)
    {
        // Legacy IHostBuilder path (e.g. Host.CreateDefaultBuilder().ConfigureWebHostDefaults(...))
        if (hostBuilder is IHostBuilder legacyBuilder)
        {
            legacyBuilder.ConfigureServices((_, services) =>
            {
                services.AddSingleton<IServer, NoOpServer>();
                RemoveHostedServices(services);
            });
            return;
        }

        // HostApplicationBuilder / WebApplicationBuilder (.NET 7+ minimal hosting model).
        // We can't reference the type directly (it's in the target app's framework),
        // so access the Services property via reflection.
        var servicesProperty = hostBuilder.GetType().GetProperty("Services");
        if (servicesProperty?.GetValue(hostBuilder) is IServiceCollection services)
        {
            services.AddSingleton<IServer, NoOpServer>();
            RemoveHostedServices(services);
        }
    }

    private static void RemoveHostedServices(IServiceCollection services)
    {
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
    }

    private static IServiceProvider? GetServiceProviderFallback(Assembly assembly)
    {
        // Convention-based approach for apps with a Startup class
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

/// <summary>
/// Minimal <see cref="IObserver{T}"/> that delegates <see cref="OnNext"/> to an <see cref="Action{T}"/>.
/// </summary>
internal sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
