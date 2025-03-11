using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using SwaggerDiff.AspNetCore.Models;
using SwaggerDiff.AspNetCore.Services;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace SwaggerDiff.AspNetCore.Extensions;

public static class SwaggerDiffExtensions
{
    private static readonly System.Reflection.Assembly Assembly = typeof(SwaggerDiffExtensions).Assembly;
    private static readonly string AssemblyName = Assembly.GetName().Name!;

    // MSBuild converts hyphens to underscores in directory-level embedded resource names
    private static string SwaggerDiffToolResource => $"{AssemblyName}.wwwroot.swagger-diff-tool.html";
    private static string SwaggerDiffIndexResource => $"{AssemblyName}.wwwroot.swagger_diff.index.html";
    private static string SwaggerDiffFolderResource => $"{AssemblyName}.wwwroot.swagger_diff";

    /// <summary>
    /// Injects the Swagger Diff Tool button into the Swagger UI.
    /// Call this inside your SwaggerUI configuration.
    /// </summary>
    public static void AddSwaggerDiffButton(this SwaggerUIOptions options)
    {
        using var stream = Assembly.GetManifestResourceStream(SwaggerDiffToolResource);

        if (stream == null) return;

        using var reader = new StreamReader(stream);
        options.HeadContent = reader.ReadToEnd();
    }

    /// <summary>
    /// Maps the Swagger Diff UI and API endpoints.
    /// Call this after building the WebApplication.
    /// </summary>
    public static WebApplication UseSwaggerDiff(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<SwaggerDiffOptions>>().Value;

        // Map API endpoints
        app.MapGet("/api-docs/versions", (SwaggerDiffService service) =>
        {
            var versions = service.GetAvailableVersions();
            return Results.Ok(new { isSuccess = true, data = versions });
        }).ExcludeFromDescription();

        app.MapPost("/api-docs/compare", async (ApiDiffRequest request, SwaggerDiffService service) =>
        {
            var result = await service.GetDiffAsync(request);

            return result == null
                ? Results.Ok(new { isSuccess = false, message = "Failed to retrieve the diff." })
                : Results.Ok(new { isSuccess = true, data = result });
        }).ExcludeFromDescription();

        // Serve the Swagger Diff UI
        app.Map(options.RoutePrefix, swaggerDiffApp =>
        {
            swaggerDiffApp.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new EmbeddedFileProvider(Assembly, SwaggerDiffFolderResource),
                RequestPath = ""
            });

            swaggerDiffApp.Run(async context =>
            {
                if (context.Request.Path == "/" || context.Request.Path == "")
                {
                    await using var stream = Assembly.GetManifestResourceStream(SwaggerDiffIndexResource);
                    if (stream != null)
                    {
                        context.Response.ContentType = "text/html";
                        await stream.CopyToAsync(context.Response.Body);
                        return;
                    }
                }

                context.Response.StatusCode = 404;
            });
        });

        return app;
    }
}
