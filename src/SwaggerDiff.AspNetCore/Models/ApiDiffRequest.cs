namespace SwaggerDiff.AspNetCore.Models;

public class ApiDiffRequest
{
    public string OldVersionName { get; set; } = string.Empty;

    public string NewVersionName { get; set; } = string.Empty;

    public ApiComparisonType ComparisonType { get; set; } = ApiComparisonType.Diff;
}
