namespace SwaggerDiff.AspNetCore;

/// <summary>
/// Provides environment detection helpers for use in your application's startup code.
/// <para>
/// When the <c>swaggerdiff snapshot</c> CLI tool generates a snapshot, it boots your application's
/// host to resolve the Swagger/OpenAPI document. During this process external dependencies
/// (secret vaults, databases, message brokers, etc.) may be unreachable or unnecessary.
/// </para>
/// <example>
/// Wrap expensive or environment-dependent startup code so it is skipped during snapshot generation:
/// <code>
/// if (!SwaggerDiffEnv.IsDryRun)
/// {
///     builder.ConfigureSecretVault();
///     builder.Services.AddDbContext&lt;AppDbContext&gt;(...);
/// }
/// </code>
/// </example>
/// </summary>
public static class SwaggerDiffEnv
{
    /// <summary>
    /// The environment variable set by the <c>swaggerdiff</c> CLI tool when it boots
    /// the target application to generate a snapshot.
    /// </summary>
    public const string DryRunVariable = "SWAGGERDIFF_DRYRUN";

    /// <summary>
    /// Returns <c>true</c> when the application is being loaded by the <c>swaggerdiff</c>
    /// CLI tool for snapshot generation. Use this to skip external configuration providers,
    /// database connections, or other side-effects that are not needed for OpenAPI generation.
    /// </summary>
    public static bool IsDryRun =>
        string.Equals(
            Environment.GetEnvironmentVariable(DryRunVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
