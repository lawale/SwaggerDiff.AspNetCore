using Microsoft.Extensions.DependencyInjection;
using SwaggerDiff.AspNetCore.Services;

namespace SwaggerDiff.AspNetCore.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Swagger Diff services (diff client, diff service, and options).
    /// </summary>
    public static IServiceCollection AddSwaggerDiff(this IServiceCollection services, Action<SwaggerDiffOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<SwaggerDiffOptions>(_ => { });

        services.AddTransient<IApiDiffClient, OasDiffClient>();
        services.AddTransient<SwaggerDiffService>();

        return services;
    }
}
