using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;

namespace SwaggerDiff.Tool;

/// <summary>
/// A no-op server implementation that doesn't bind any ports.
/// Used when loading assemblies for swagger generation only.
/// </summary>
internal sealed class NoOpServer : IServer
{
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        // No resources to dispose
    }
}