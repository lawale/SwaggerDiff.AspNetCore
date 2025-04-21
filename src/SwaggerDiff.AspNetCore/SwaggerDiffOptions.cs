namespace SwaggerDiff.AspNetCore;

public class SwaggerDiffOptions
{
    /// <summary>
    /// Relative path (from AppDomain.CurrentDomain.BaseDirectory) to the directory containing versioned swagger snapshots.
    /// </summary>
    public string VersionsDirectory { get; set; } = Path.Combine("Docs", "Versions");

    /// <summary>
    /// Glob pattern used to find snapshot files inside <see cref="VersionsDirectory"/>.
    /// </summary>
    public string FilePattern { get; set; } = "doc_*.json";

    /// <summary>
    /// The route prefix where the Swagger Diff UI is served.
    /// </summary>
    public string RoutePrefix { get; set; } = "/swagger-diff";

    /// <summary>
    /// Explicit path to the oasdiff binary. When set, skips PATH lookup and auto-download.
    /// </summary>
    public string? OasDiffPath { get; set; }

    /// <summary>
    /// The oasdiff version to auto-download if it is not found on PATH.
    /// </summary>
    public string OasDiffVersion { get; set; } = "1.11.10";
}
