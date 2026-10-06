using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectRemnants.Setup.Llm;

public sealed class LlamaCppDriver : IAsyncDisposable
{
    private static readonly Uri LatestRelease = new(
        "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
    private static readonly string PortableDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectRemnants", "LlamaCpp");
    private const string PortableMarker = ".project-remnants-portable";

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{DefaultPort}/"),
        Timeout = TimeSpan.FromSeconds(5)
    };
    private Process? _ownedServer;

    public const int DefaultPort = 8089;
    public string? ExecutablePath { get; private set; }
    public bool OwnsServer => _ownedServer is { HasExited: false };
    public string Endpoint => $"http://127.0.0.1:{DefaultPort}/v1/chat/completions";

    public static string ExecutableFileName =>
        OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    public string? FindExecutable()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        candidates.AddRange((path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim().Trim('"'), ExecutableFileName)));
        var portable = FindExecutableIn(PortableDirectory);
        if (portable is not null)
        {
            candidates.Add(portable);
        }

        ExecutablePath = candidates.FirstOrDefault(File.Exists);
        return ExecutablePath;
    }

    private static string? FindExecutableIn(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, ExecutableFileName, SearchOption.AllDirectories)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<string> InstallAsync(
        IProgress<OllamaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InstallVariantAsync(preferred: true, progress, cancellationToken);
        }
        catch (Exception exception) when (!IsCpuPreference())
        {
            progress?.Report(new OllamaProgress(
                $"The GPU build failed ({exception.Message}). Falling back to the CPU build.", null, true));
            return await InstallVariantAsync(preferred: false, progress, cancellationToken);
        }
    }

    private static bool IsCpuPreference()
    {
        // Falls back to the CPU build whenever no GPU was detected.
        return OllamaDriver.DetectVramGb() <= 0 && !HasNvidiaSmi();
    }

    private static bool HasNvidiaSmi() =>
        !string.IsNullOrWhiteSpace(RunProcess(
            OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi",
            "--query-gpu=name", "--format=csv,noheader"));

    private static string? RunProcess(string file, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string> InstallVariantAsync(
        bool preferred,
        IProgress<OllamaProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (url, archiveName) = await ResolveAssetAsync(preferred, cancellationToken);
        var parent = Path.GetDirectoryName(PortableDirectory)!;
        var staging = Path.Combine(parent, $".LlamaCpp-{Guid.NewGuid():N}");
        var archive = Path.Combine(Path.GetTempPath(), archiveName);
        try
        {
            Directory.CreateDirectory(staging);
            progress?.Report(new OllamaProgress($"Downloading llama.cpp from {url.Host}", 0, true));
            using (var download = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            using (var response = await download.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidOperationException("The llama.cpp download was not secure.");
                }

                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(
                    archive, FileMode.Create, FileAccess.Write, FileShare.None,
                    1_048_576, FileOptions.Asynchronous))
                {
                    var buffer = new byte[1_048_576];
                    long received = 0;
                    var lastReported = -1;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        received += read;
                        if (total > 0)
                        {
                            var percent = (int)(received * 100 / total);
                            if (percent != lastReported)
                            {
                                lastReported = percent;
                                progress?.Report(new OllamaProgress("Downloading llama.cpp", percent));
                            }
                        }
                    }
                }
            }

            progress?.Report(new OllamaProgress("Installing llama.cpp", null, true));
            if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            }
            else
            {
                await using var file = File.OpenRead(archive);
                await using var gzip = new System.IO.Compression.GZipStream(
                    file, CompressionMode.Decompress);
                Voice.VoiceInstaller.ValidateTarEntries(gzip, cancellationToken);
                await using var verify = File.OpenRead(archive);
                await using var gzip2 = new System.IO.Compression.GZipStream(
                    verify, CompressionMode.Decompress);
                System.Formats.Tar.TarFile.ExtractToDirectory(gzip2, staging, overwriteFiles: true);
            }

            var executable = Directory.EnumerateFiles(
                staging, ExecutableFileName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("The llama.cpp package does not contain llama-server.");
            var relative = Path.GetRelativePath(staging, executable);
            File.WriteAllText(Path.Combine(staging, PortableMarker), string.Empty);
            if (Directory.Exists(PortableDirectory))
            {
                if (!File.Exists(Path.Combine(PortableDirectory, PortableMarker)))
                {
                    throw new InvalidOperationException(
                        $"The llama.cpp folder is not owned by this app: {PortableDirectory}");
                }

                Directory.Delete(PortableDirectory, recursive: true);
            }

            Directory.Move(staging, PortableDirectory);
            ExecutablePath = Path.Combine(PortableDirectory, relative);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(ExecutablePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception)
                {
                }
            }

            return ExecutablePath;
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<(Uri Url, string ArchiveName)> ResolveAssetAsync(
        bool preferred, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { { "User-Agent", "ProjectRemnantsSetup" } }
        };
        var json = await client.GetStringAsync(LatestRelease, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var assets = document.RootElement.GetProperty("assets").EnumerateArray()
            .Select(asset => (
                Name: asset.GetProperty("name").GetString() ?? string.Empty,
                Url: asset.GetProperty("browser_download_url").GetString() ?? string.Empty))
            .ToArray();

        var suffixes = CandidateSuffixes(preferred);
        foreach (var suffix in suffixes)
        {
            var hit = assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (hit.Name.Length != 0 && Uri.TryCreate(hit.Url, UriKind.Absolute, out var url))
            {
                return (url, hit.Name);
            }
        }

        throw new InvalidOperationException(
            "No matching llama.cpp release asset was found for this system.");
    }

    private static IEnumerable<string> CandidateSuffixes(bool preferred)
    {
        var amd = OllamaDriverPrefersAmd();
        var nvidia = HasNvidiaSmi();
        if (!preferred)
        {
            yield return OperatingSystem.IsWindows() ? "-bin-win-cpu-x64.zip" : "-bin-ubuntu-x64.tar.gz";
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            if (amd)
            {
                yield return "-bin-win-rocm-10.0-x64.zip";
            }

            if (nvidia)
            {
                yield return "-bin-win-cuda-13.4-x64.zip";
            }

            yield return "-bin-win-cpu-x64.zip";
            yield break;
        }

        if (amd)
        {
            yield return "-bin-ubuntu-rocm-10.0-x64.tar.gz";
        }

        if (nvidia)
        {
            yield return "-bin-ubuntu-cuda-13.4-x64.tar.gz";
        }

        yield return "-bin-ubuntu-x64.tar.gz";
    }

    private static bool OllamaDriverPrefersAmd()
    {
        var nvidia = RunProcess(
            OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi",
            "--query-gpu=name", "--format=csv,noheader");
        if (!string.IsNullOrWhiteSpace(nvidia))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            var lspci = RunProcess("lspci") ?? string.Empty;
            return lspci.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                lspci.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        }

        var controllers = RunProcess("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue).Name");
        return controllers?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true ||
            controllers?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task EnsureServerAsync(
        string modelPath, IProgress<OllamaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken))
        {
            return;
        }

        var executable = ExecutablePath ?? FindExecutable()
            ?? throw new FileNotFoundException("llama.cpp is not installed.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
        };
#if WINDOWS
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
#endif
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(Path.GetFullPath(modelPath));
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(DefaultPort.ToString());
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add("4096");
        _ownedServer = Process.Start(startInfo)
            ?? throw new InvalidOperationException("llama-server did not start.");

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1_000, cancellationToken);
            if (_ownedServer.HasExited)
            {
                throw new InvalidOperationException(
                    $"llama-server exited with code {_ownedServer.ExitCode} while loading the model.");
            }

            if (await IsReadyAsync(cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "llama-server did not become ready within 10 minutes.");
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("v1/models", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<string?> GetServedModelNameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await _http.GetStringAsync("v1/models", cancellationToken));
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } model &&
                model.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    public async Task WarmModelAsync(
        string model, IProgress<OllamaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OllamaProgress($"Loading {model} into memory", null, true));
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{DefaultPort}/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        using var response = await client.PostAsJsonAsync("v1/chat/completions", new
        {
            model,
            messages = new[] { new { role = "user", content = "hi" } },
            max_tokens = 1,
            stream = false
        }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"llama-server could not load {model}: HTTP {(int)response.StatusCode}.");
        }

        progress?.Report(new OllamaProgress($"{model} is ready for in-game use", 100, true));
    }

    public async Task StopOwnedServerAsync()
    {
        var server = _ownedServer;
        _ownedServer = null;
        if (server is null)
        {
            return;
        }

        try
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync();
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            server.Dispose();
        }
    }

    public void UninstallPortable()
    {
        if (!Directory.Exists(PortableDirectory) ||
            !File.Exists(Path.Combine(PortableDirectory, PortableMarker)))
        {
            return;
        }

        try
        {
            foreach (var process in Process.GetProcessesByName("llama-server"))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null)
                    {
                        var relative = Path.GetRelativePath(PortableDirectory, Path.GetFullPath(path));
                        if (!Path.IsPathRooted(relative) &&
                            relative != ".." &&
                            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(5_000);
                        }
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
        }

        Directory.Delete(PortableDirectory, recursive: true);
        if (ExecutablePath is not null &&
            Path.GetFullPath(ExecutablePath).StartsWith(
                Path.GetFullPath(PortableDirectory),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            ExecutablePath = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopOwnedServerAsync();
        _http.Dispose();
    }
}
