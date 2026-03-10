# SwaggerDiff.Tool

A `dotnet tool` CLI that generates OpenAPI snapshots from **built** assemblies without starting the web server. Think `dotnet ef migrations add`, but for your API surface.

## Prerequisites

- .NET 8.0+
- [Swashbuckle.AspNetCore](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) configured in your API project (used to resolve `ISwaggerProvider`)

## Installation

```bash
# Local (per-repo)
dotnet new tool-manifest
dotnet tool install SwaggerDiff.Tool

# Global
dotnet tool install -g SwaggerDiff.Tool
```

## Commands

### `swaggerdiff snapshot`

Generate a new OpenAPI snapshot. The simplest usage — run from your project directory:

```bash
# Auto-discovers the .csproj, builds it, and generates a snapshot
swaggerdiff snapshot
```

With explicit project and configuration:

```bash
swaggerdiff snapshot --project ./src/MyApi/MyApi.csproj -c Release --output Docs/Versions
```

Or point directly at pre-built assemblies (skips build and project discovery entirely):

```bash
# Single assembly
swaggerdiff snapshot --assembly ./bin/Release/net8.0/MyApi.dll

# Multiple assemblies
swaggerdiff snapshot \
  --assembly ./src/AdminApi/bin/Release/net9.0/AdminApi.dll \
  --assembly ./src/TenantApi/bin/Release/net9.0/TenantApi.dll
```

| Option | Default | Description |
|--------|---------|-------------|
| `--project` | auto-discover | Path to one or more `.csproj` files. Repeat for multiple projects |
| `--assembly` | — | Direct path to built DLL(s). Overrides `--project` and skips build + project discovery. Repeat for multiple |
| `-c`, `--configuration` | `Debug` | Build configuration (used with `--project`) |
| `--no-build` | `false` | Skip the build step (assumes the project was already built) |
| `--output` | `Docs/Versions` | Directory where snapshots are written (relative to each project directory) |
| `--doc-name` | `v1` | Swagger document name passed to `ISwaggerProvider.GetSwagger()` |
| `--exclude` | — | Project names to exclude from auto-discovery (without `.csproj`). Repeat for multiple |
| `--exclude-dir` | — | Directory names to exclude from auto-discovery. Repeat for multiple |
| `--parallelism` | `0` | Maximum concurrent snapshot subprocesses. `0` means unlimited (one per project) |

The command will:

1. **Build the project** (unless `--no-build` or `--assembly` is used), then resolve the output DLL via MSBuild.
2. **Load the assembly** and build the host — your `Program.cs` entry point runs, but a `NoOpServer` replaces Kestrel so no ports are bound and hosted services are stripped out.
3. **Resolve `ISwaggerProvider`** from the DI container and serialize the OpenAPI document.
4. **Compare** with the latest existing snapshot (normalizing away the `info.version` field).
5. If the API surface has changed, **write a new timestamped file** (e.g. `doc_20250612143022.json`). If nothing changed, print "No API changes detected" and exit cleanly.

### Multiple projects

When run from a solution directory (or any directory without a single `.csproj`), the tool automatically discovers all ASP.NET Core web projects by scanning up to 2 levels deep for `.csproj` files with `Sdk="Microsoft.NET.Sdk.Web"`.

```bash
# From the solution root — discovers and snapshots all web API projects
swaggerdiff snapshot

# Explicit multi-project
swaggerdiff snapshot \
  --project ./src/AdminApi/AdminApi.csproj \
  --project ./src/TenantApi/TenantApi.csproj

# Auto-discover but skip specific projects or directories
swaggerdiff snapshot --exclude MyApi.Tests --exclude-dir tests
```

Each project's snapshots are written to `Docs/Versions/` relative to that project's own directory:

```
src/
  ServiceOneApi/
    Docs/Versions/doc_20250612143022.json
  ServiceTwoApi/
    Docs/Versions/doc_20250612143022.json
```

The tool skips `bin/`, `obj/`, `.git/`, and other well-known non-project directories automatically.

### CI / Docker optimisation

For CI pipelines or Docker builds where the solution is already built, you can combine `--assembly` with `--no-build` and `--parallelism` to skip all discovery and build overhead:

```bash
# Pre-build the solution once, then snapshot all APIs
dotnet build MySolution.sln -c Release

swaggerdiff snapshot --no-build -c Release \
  --assembly ./src/AdminApi/bin/Release/net9.0/AdminApi.dll \
  --assembly ./src/AuthApi/bin/Release/net9.0/AuthApi.dll \
  --assembly ./src/TenantApi/bin/Release/net9.0/TenantApi.dll \
  --parallelism 2
```

When `--assembly` is used, the tool skips project discovery and MSBuild property evaluation entirely — it goes straight to launching snapshot subprocesses. The output directory for each assembly is inferred by navigating up from `bin/{Config}/{TFM}/` to the project root.

When `--no-build` is used with `--project` (instead of `--assembly`), MSBuild property resolution runs in parallel since it's read-only.

Use `--parallelism` to cap concurrent subprocesses. The default (`0`) runs all concurrently. On resource-constrained environments like CI runners with 2 vCPUs, `--parallelism 2` avoids CPU oversubscription.

### `swaggerdiff list`

List available snapshots:

```bash
swaggerdiff list --dir Docs/Versions
```

| Option | Default | Description |
|--------|---------|-------------|
| `--dir` | `Docs/Versions` | Directory to scan for snapshot files |

## Dry-run mode — skipping external dependencies

When the CLI tool loads your application to generate a snapshot, your `Program.cs` entry point runs in full. This means any startup code that connects to external services (secret vaults, databases, message brokers, etc.) will execute and may fail if those services are unreachable.

To handle this, the tool automatically sets a `SWAGGERDIFF_DRYRUN` environment variable. The companion library provides a convenient static helper to check it:

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
```

`SwaggerDiffEnv.IsDryRun` returns `true` only when the application is being loaded by the `swaggerdiff` CLI tool. During normal `dotnet run`, it returns `false` and all your startup code runs as usual.

## How assembly loading works

The CLI uses a **two-stage subprocess** pattern (similar to how the Swashbuckle CLI works):

1. **Stage 1** (`snapshot`): Builds the project (if needed), resolves the output DLL via `dotnet msbuild --getProperty:TargetPath`, then re-invokes itself via `dotnet exec --depsfile <app>.deps.json --additional-deps <tool>.deps.json --runtimeconfig <app>.runtimeconfig.json <tool>.dll _snapshot ...`. This ensures the tool runs inside the target app's dependency graph while retaining access to its own dependencies.

2. **Stage 2** (`_snapshot`): Now running with the correct dependencies, it loads the assembly via `AssemblyLoadContext`, subscribes to `DiagnosticListener` events to intercept the host as it builds, injects `NoOpServer`, and extracts the swagger document from the DI container.

## Related packages

| Package | Description |
|---------|-------------|
| [`SwaggerDiff.AspNetCore`](https://www.nuget.org/packages/SwaggerDiff.AspNetCore) | Library that embeds a diff viewer UI and wires up minimal API endpoints for any ASP.NET Core project |

## License

MIT
