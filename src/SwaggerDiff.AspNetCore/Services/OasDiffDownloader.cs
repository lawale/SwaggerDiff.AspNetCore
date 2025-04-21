using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwaggerDiff.AspNetCore.Services;

/// <summary>
/// Manages the oasdiff binary — locates it on PATH or auto-downloads it on first use.
/// Downloaded binaries are cached in ~/.swaggerdiff/bin/{version}/.
/// </summary>
public class OasDiffDownloader
{
    private readonly SwaggerDiffOptions _options;
    private readonly ILogger<OasDiffDownloader> _logger;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public OasDiffDownloader(IOptions<SwaggerDiffOptions> options, ILogger<OasDiffDownloader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns the full path to the oasdiff binary, downloading it if necessary.
    /// </summary>
    public async Task<string> GetOasDiffPathAsync()
    {
        // 1. If the user configured an explicit path, use that
        if (!string.IsNullOrEmpty(_options.OasDiffPath))
        {
            if (File.Exists(_options.OasDiffPath))
                return _options.OasDiffPath;

            throw new FileNotFoundException(
                $"Configured OasDiffPath does not exist: {_options.OasDiffPath}");
        }

        // 2. Check if oasdiff is already on PATH
        var pathBinary = FindOnPath("oasdiff");
        if (pathBinary != null)
        {
            _logger.LogDebug("Found oasdiff on PATH: {Path}", pathBinary);
            return pathBinary;
        }

        // 3. Check the local cache
        var cachedPath = GetCachedBinaryPath();
        if (File.Exists(cachedPath))
        {
            _logger.LogDebug("Using cached oasdiff: {Path}", cachedPath);
            return cachedPath;
        }

        // 4. Download it
        await _downloadLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock (another thread may have downloaded it)
            if (File.Exists(cachedPath))
                return cachedPath;

            await DownloadAsync(cachedPath);
            return cachedPath;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task DownloadAsync(string targetPath)
    {
        var version = _options.OasDiffVersion;
        var (os, arch) = GetPlatformIdentifier();
        var fileName = $"oasdiff_{version}_{os}_{arch}.tar.gz";
        var url = $"https://github.com/oasdiff/oasdiff/releases/download/v{version}/{fileName}";

        _logger.LogInformation("Downloading oasdiff v{Version} from {Url} ...", version, url);

        var targetDir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDir);

        var tempTarGz = Path.Combine(targetDir, fileName);

        try
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Failed to download oasdiff: HTTP {(int)response.StatusCode} from {url}. " +
                    "You can install oasdiff manually (https://github.com/oasdiff/oasdiff) " +
                    "or set SwaggerDiffOptions.OasDiffPath to point to an existing binary.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(tempTarGz);
            await stream.CopyToAsync(fileStream);

            _logger.LogDebug("Download complete, extracting...");
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException(
                $"Timed out downloading oasdiff from {url}. " +
                "You can install oasdiff manually or set SwaggerDiffOptions.OasDiffPath.");
        }

        // Extract the binary from the tar.gz
        try
        {
            await using var gzipStream = new GZipStream(File.OpenRead(tempTarGz), CompressionMode.Decompress);
            await using var tarReader = new TarReader(gzipStream);

            var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "oasdiff.exe" : "oasdiff";

            while (await tarReader.GetNextEntryAsync() is { } entry)
            {
                if (entry.Name.EndsWith(binaryName, StringComparison.OrdinalIgnoreCase) && entry.EntryType == TarEntryType.RegularFile)
                {
                    await using var entryStream = entry.DataStream!;
                    await using var output = File.Create(targetPath);
                    await entryStream.CopyToAsync(output);
                    break;
                }
            }

            if (!File.Exists(targetPath))
            {
                throw new InvalidOperationException(
                    $"Could not find '{binaryName}' inside the downloaded archive. " +
                    "The release format may have changed.");
            }

            // Make executable on Unix
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(targetPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            _logger.LogInformation("oasdiff v{Version} installed to {Path}", _options.OasDiffVersion, targetPath);
        }
        finally
        {
            // Clean up the tarball
            if (File.Exists(tempTarGz))
                File.Delete(tempTarGz);
        }
    }

    private string GetCachedBinaryPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "oasdiff.exe" : "oasdiff";
        return Path.Combine(home, ".swaggerdiff", "bin", _options.OasDiffVersion, binaryName);
    }

    private static (string os, string arch) GetPlatformIdentifier()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin"
            : "linux";

        // macOS releases are universal binaries ("darwin_all")
        if (os == "darwin")
            return (os, "all");

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"Unsupported architecture: {RuntimeInformation.OSArchitecture}. " +
                "Install oasdiff manually and set SwaggerDiffOptions.OasDiffPath.")
        };

        return (os, arch);
    }

    private static string? FindOnPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in pathEnv.Split(separator))
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(dir, executable + ext);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }
}
