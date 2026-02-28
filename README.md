# SwaggerDiff.AspNetCore

An in-app OpenAPI diff viewer and snapshot CLI for ASP.NET Core APIs.

Compare versioned OpenAPI snapshots side-by-side from within your running application and generate those snapshots from the command line without ever starting the web server.

## Packages

| Package | Description |
|---------|-------------|
| `SwaggerDiff.AspNetCore` | Library. Embeds a diff viewer UI and wires up minimal API endpoints for any ASP.NET Core project. |
| `SwaggerDiff.Tool` | CLI tool. Generates timestamped OpenAPI snapshots from a built assembly (no running server required). |

## Prerequisites

- .NET 8.0+
- [Swashbuckle.AspNetCore](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) configured in your API project (used by the CLI tool to resolve `ISwaggerProvider`)

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

## Library — `SwaggerDiff.AspNetCore`

### Installation

Add a project reference or (when published) install via NuGet:

```bash
dotnet add package SwaggerDiff.AspNetCore
```

### Setup

#### 1. Register services

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

#### 2. Map the UI and endpoints

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

### How it works

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

### Snapshot directory

The library expects versioned JSON files in a directory relative to `AppDomain.CurrentDomain.BaseDirectory`:

```
bin/Debug/net8.0/
  Docs/
    Versions/
      doc_20250101120000.json
      doc_20250115093000.json
```

The filenames (minus `.json`) appear as version options in the diff viewer's dropdowns.

### Options reference

| Property | Default | Description |
|----------|---------|-------------|
| `VersionsDirectory` | `Docs/Versions` | Path (relative to base directory) containing snapshot files |
| `FilePattern` | `doc_*.json` | Glob pattern for discovering snapshot files |
| `RoutePrefix` | `/swagger-diff` | URL path where the diff viewer UI is served |
| `OasDiffPath` | `null` | Explicit path to an oasdiff binary. Skips PATH lookup and auto-download when set |
| `OasDiffVersion` | `1.11.10` | oasdiff version to auto-download if not found on PATH |

---

## CLI Tool — `SwaggerDiff.Tool`

A `dotnet tool` that generates OpenAPI snapshots from a **built** assembly without starting the web server. Think `dotnet ef migrations add`, but for your API surface.

### Installation

```bash
# Local (per-repo)
dotnet new tool-manifest
dotnet tool install SwaggerDiff.Tool

# Global
dotnet tool install -g SwaggerDiff.Tool
```

### Commands

#### `swaggerdiff snapshot`

Generate a new OpenAPI snapshot. The simplest usage — run from your project directory:

```bash
# Auto-discovers the .csproj, builds it, and generates a snapshot
swaggerdiff snapshot
```

With explicit project and configuration:

```bash
swaggerdiff snapshot --project ./src/MyApi/MyApi.csproj -c Release --output Docs/Versions
```

Or point directly at a pre-built assembly:

```bash
swaggerdiff snapshot --assembly ./bin/Release/net8.0/MyApi.dll
```

| Option | Default | Description |
|--------|---------|-------------|
| `--project` | auto-discover | Path to a `.csproj` file. If omitted, finds the single `.csproj` in the current directory |
| `--assembly` | — | Direct path to a built DLL. Overrides `--project` and skips the build step |
| `-c`, `--configuration` | `Debug` | Build configuration (used with `--project`) |
| `--no-build` | `false` | Skip the build step (assumes the project was already built) |
| `--output` | `Docs/Versions` | Directory where snapshots are written |
| `--doc-name` | `v1` | Swagger document name passed to `ISwaggerProvider.GetSwagger()` |

The command will:

1. **Build the project** (unless `--no-build` or `--assembly` is used), then resolve the output DLL via MSBuild.
2. **Load the assembly** and build the host — your `Program.cs` entry point runs, but a `NoOpServer` replaces Kestrel so no ports are bound and hosted services are stripped out.
3. **Resolve `ISwaggerProvider`** from the DI container and serialize the OpenAPI document.
4. **Compare** with the latest existing snapshot (normalizing away the `info.version` field).
5. If the API surface has changed, **write a new timestamped file** (e.g. `doc_20250612143022.json`). If nothing changed, print "No API changes detected" and exit cleanly.

### Dry-run mode — skipping external dependencies

When the CLI tool loads your application to generate a snapshot, your `Program.cs` entry point runs in full. This means any startup code that connects to external services (secret vaults, databases, message brokers, etc.) will execute and may fail if those services are unreachable.

To handle this, the tool automatically sets a `SWAGGERDIFF_DRYRUN` environment variable. The library provides a convenient static helper to check it:

```csharp
using SwaggerDiff.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

if (!SwaggerDiffEnv.IsDryRun)
{
    // These only run during normal application startup — not during snapshot generation
    builder.ConfigureSecretVault();
    builder.Services.AddDbContext<AppDbContext>(...);
    builder.Services.ConfigureMessageBroker();
}

// Swagger registration always runs — this is what the tool needs
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSwaggerDiff();
```

`SwaggerDiffEnv.IsDryRun` returns `true` only when the application is being loaded by the `swaggerdiff` CLI tool. During normal `dotnet run`, it returns `false` and all your startup code runs as usual.

#### `swaggerdiff list`

List available snapshots:

```bash
swaggerdiff list --dir Docs/Versions
```

| Option | Default | Description |
|--------|---------|-------------|
| `--dir` | `Docs/Versions` | Directory to scan for snapshot files |

### How assembly loading works

The CLI uses a **two-stage subprocess** pattern (similar to how the Swashbuckle CLI works):

1. **Stage 1** (`snapshot`): Builds the project (if needed), resolves the output DLL via `dotnet msbuild --getProperty:TargetPath`, then re-invokes itself via `dotnet exec --depsfile <app>.deps.json --additional-deps <tool>.deps.json --runtimeconfig <app>.runtimeconfig.json <tool>.dll _snapshot ...`. This ensures the tool runs inside the target app's dependency graph while retaining access to its own dependencies.

2. **Stage 2** (`_snapshot`): Now running with the correct dependencies, it loads the assembly via `AssemblyLoadContext`, subscribes to `DiagnosticListener` events to intercept the host as it builds, injects `NoOpServer`, and extracts the swagger document from the DI container.

---

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

```bash
# Generate a snapshot (builds the project automatically)
swaggerdiff snapshot --output ./Docs/Versions

# Copy snapshots to build output so the UI can find them
cp -r Docs/ bin/Debug/net8.0/Docs/

# Run the app and navigate to /swagger-diff
dotnet run
```

---

## Publishing

Both packages are published to [NuGet.org](https://www.nuget.org/) via GitHub Actions.

### Setup

Add a `NUGET_API_KEY` secret to your GitHub repository:

1. Generate an API key at [nuget.org/account/apikeys](https://www.nuget.org/account/apikeys) with push permissions for `SwaggerDiff.AspNetCore` and `SwaggerDiff.Tool`
2. Go to your repo **Settings > Secrets and variables > Actions**
3. Add a new secret named `NUGET_API_KEY` with the key value

### Release via tag push

Tag a commit and push it — the release workflow builds, packs, publishes to NuGet.org, and creates a GitHub Release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The version number is derived from the tag (strips the `v` prefix). Both `SwaggerDiff.AspNetCore` and `SwaggerDiff.Tool` are published with the same version.

### Release via manual dispatch

For pre-release or testing builds, trigger the workflow manually from the **Actions** tab:

1. Go to **Actions > Release > Run workflow**
2. Enter a version string (e.g. `1.1.0-beta.1`)
3. Click **Run workflow**

### CI

Every push to `master` and every pull request runs the CI workflow which builds, packs (to verify packaging works), and uploads the `.nupkg` files as artifacts.

### Local packing

To build packages locally:

```bash
dotnet pack --configuration Release --output ./artifacts
```

## License

MIT
