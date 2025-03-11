using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SwaggerDiff.AspNetCore.Models;

namespace SwaggerDiff.AspNetCore.Services;

public class OasDiffClient : IApiDiffClient
{
    private readonly ILogger<OasDiffClient> _logger;

    public OasDiffClient(ILogger<OasDiffClient> logger)
    {
        _logger = logger;
    }

    public async Task<string?> GetDiffAsync(string fileOnePath, string fileTwoPath, ApiComparisonType action)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "oasdiff",
                    Arguments = $"{action.ToString().ToLower()} {fileOnePath} {fileTwoPath} -f html",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0 || string.IsNullOrWhiteSpace(error)) return output;
            _logger.LogCritical("Error calling OAS Diff: {Error}", error);
            return null;
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "An Error Occurred Generating DIFF");
            return null;
        }
    }
}
