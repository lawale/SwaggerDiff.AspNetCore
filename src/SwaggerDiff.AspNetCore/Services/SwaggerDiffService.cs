using Microsoft.Extensions.Options;
using SwaggerDiff.AspNetCore.Models;

namespace SwaggerDiff.AspNetCore.Services;

public class SwaggerDiffService
{
    private readonly IApiDiffClient _apiDiffClient;
    private readonly SwaggerDiffOptions _options;

    public SwaggerDiffService(IApiDiffClient apiDiffClient, IOptions<SwaggerDiffOptions> options)
    {
        _apiDiffClient = apiDiffClient;
        _options = options.Value;
    }

    public IEnumerable<string> GetAvailableVersions()
    {
        var path = GetVersionsPath();

        if (!Directory.Exists(path))
            return [];

        return Directory.GetFiles(path, _options.FilePattern)
            .OrderDescending()
            .Select(Path.GetFileNameWithoutExtension)!;
    }

    public async Task<string?> GetDiffAsync(ApiDiffRequest request)
    {
        var fileOne = GetFilePath(request.OldVersionName);
        var fileTwo = GetFilePath(request.NewVersionName);

        if (string.IsNullOrEmpty(fileOne) || string.IsNullOrEmpty(fileTwo))
            return null;

        return await _apiDiffClient.GetDiffAsync(fileOne, fileTwo, request.ComparisonType);
    }

    private string GetVersionsPath()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDirectory, _options.VersionsDirectory);
    }

    private string? GetFilePath(string version)
    {
        var path = Path.Combine(GetVersionsPath(), $"{version}.json");
        return File.Exists(path) ? path : null;
    }
}
