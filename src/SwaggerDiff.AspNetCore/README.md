# SwaggerDiff.AspNetCore

An in-app OpenAPI diff viewer for ASP.NET Core APIs. Embeds a diff viewer UI and wires up minimal API endpoints — no controllers required.

Compare versioned OpenAPI snapshots side-by-side from within your running application.

## Prerequisites

- .NET 8.0+
- [Swashbuckle.AspNetCore](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) configured in your API project

### oasdiff

The library uses [oasdiff](https://github.com/oasdiff/oasdiff) under the hood to compute OpenAPI diffs. **You don't need to install it yourself** — on the first comparison request, the library will automatically:

1. Check if `oasdiff` is already on your `PATH`
2. If not, download the correct binary for your platform to `~/.swaggerdiff/bin/{version}/`
3. Cache it for all subsequent calls

Supported platforms for auto-download: Linux (amd64/arm64), macOS (universal), Windows (amd64/arm64).

If you prefer to manage the binary yourself, you can either install oasdiff globally or point to a specific binary:

```csharp
builder.Services.AddSwaggerDiff(options =>
{
    options.OasDiffPath = "/usr/local/bin/oasdiff";  // skip auto-download, use this binary
});
```

## Installation

```bash
dotnet add package SwaggerDiff.AspNetCore
```

## Setup

### 1. Register services

```csharp
using SwaggerDiff.AspNetCore.Extensions;

builder.Services.AddSwaggerDiff();
```

With custom options:

```csharp
builder.Services.AddSwaggerDiff(options =>
{
    options.VersionsDirectory = "Snapshots";       // default: "Docs/Versions"
    options.FilePattern = "swagger_*.json";         // default: "doc_*.json"
    options.RoutePrefix = "/api-diff";              // default: "/swagger-diff"
    options.OasDiffVersion = "1.11.10";            // default: "1.11.10"
    options.OasDiffPath = "/usr/local/bin/oasdiff"; // default: null (auto-detect/download)
});
```

### 2. Map the UI and endpoints

```csharp
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.AddSwaggerDiffButton();  // adds a "Diff Tool" button to Swagger UI
});

app.UseSwaggerDiff();  // serves the diff viewer + maps /api-docs/versions and /api-docs/compare
```

That's it. Navigate to `/swagger-diff` to see the diff viewer, or click the injected button from within Swagger UI.

## How it works

`AddSwaggerDiff()` registers the following services:

- `SwaggerDiffOptions` — configurable paths, route prefix, and oasdiff binary settings
- `OasDiffDownloader` (singleton) — locates or auto-downloads the `oasdiff` binary on first use
- `IApiDiffClient` / `OasDiffClient` — shells out to `oasdiff` to compute HTML diffs
- `SwaggerDiffService` — lists available snapshots and orchestrates comparisons

`UseSwaggerDiff()` does two things:

1. **Maps minimal API endpoints** (no controllers, no `AddControllers()` required):
   - `GET /api-docs/versions` — returns available snapshot filenames
   - `POST /api-docs/compare` — accepts `{ oldVersionName, newVersionName, comparisonType }` and returns the HTML diff
   - Both endpoints are excluded from the Swagger spec via `.ExcludeFromDescription()`

2. **Serves the embedded diff viewer** at the configured route prefix using `EmbeddedFileProvider`, so there are no loose files to deploy.

## Snapshot directory

The library expects versioned JSON files in a directory relative to `AppDomain.CurrentDomain.BaseDirectory`:

```
bin/Debug/net8.0/
  Docs/
    Versions/
      doc_20250101120000.json
      doc_20250115093000.json
```

The filenames (minus `.json`) appear as version options in the diff viewer's dropdowns.

Use the companion CLI tool [`SwaggerDiff.Tool`](https://www.nuget.org/packages/SwaggerDiff.Tool) to generate these snapshots from a built assembly without starting the web server.

## Options reference

| Property | Default | Description |
|----------|---------|-------------|
| `VersionsDirectory` | `Docs/Versions` | Path (relative to base directory) containing snapshot files |
| `FilePattern` | `doc_*.json` | Glob pattern for discovering snapshot files |
| `RoutePrefix` | `/swagger-diff` | URL path where the diff viewer UI is served |
| `OasDiffPath` | `null` | Explicit path to an oasdiff binary. Skips PATH lookup and auto-download when set |
| `OasDiffVersion` | `1.11.10` | oasdiff version to auto-download if not found on PATH |

## Full example

```csharp
// Program.cs
using SwaggerDiff.AspNetCore;
using SwaggerDiff.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

if (!SwaggerDiffEnv.IsDryRun)
{
    builder.ConfigureSecretVault();
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSwaggerDiff();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(o => o.AddSwaggerDiffButton());
app.UseSwaggerDiff();

app.MapGet("/hello", () => "world");

app.Run();
```

## Related packages

| Package | Description |
|---------|-------------|
| [`SwaggerDiff.Tool`](https://www.nuget.org/packages/SwaggerDiff.Tool) | CLI tool that generates OpenAPI snapshots from built assemblies without running the web server |

## License

MIT
