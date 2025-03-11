using SwaggerDiff.AspNetCore.Models;

namespace SwaggerDiff.AspNetCore.Services;

public interface IApiDiffClient
{
    Task<string?> GetDiffAsync(string fileOnePath, string fileTwoPath, ApiComparisonType action);
}
