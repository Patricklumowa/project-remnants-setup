using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectRemnants.Setup.Llm;

public sealed record OllamaProgress(string Message, int? Percent = null, bool Log = false);

public sealed class OllamaDriver : IAsyncDisposable
{
    private static readonly Uri Server = new("http://127.0.0.1:11434/");
    private static readonly Uri WindowsCli = new("https://ollama.com/download/ollama-windows-amd64.zip");
    private static readonly Uri WindowsAmd = new("https://ollama.com/download/ollama-windows-amd64-rocm.zip");
    private static readonly string PortableDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectRemnants", "Ollama");
    private const string PortableMarker = ".project-remnants-portable";
    private readonly HttpClient _http = new() { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(5) };
    private Process? _ownedServer;

    public string? ExecutablePath { get; private set; }
    public bool OwnsServer => _ownedServer is { HasExited: false };

    public string? FindExecutable()
    {
        var candidates = new List<string>();
        foreach (var path in new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        })
        {
            candidates.AddRange((path ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim().Trim('"'), "ollama.exe")));
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ollama", "ollama.exe"));
        candidates.Add(Path.Combine(PortableDirectory, "ollama.exe"));
        ExecutablePath = candidates.FirstOrDefault(File.Exists);
        return ExecutablePath;
    }

    public async Task<string> InstallAsync(
        IProgress<OllamaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(PortableDirectory)!;
        var staging = Path.Combine(parent, $".Ollama-{Guid.NewGuid():N}");
        var archive = Path.Combine(Path.GetTempPath(), $"Ollama-{Guid.NewGuid():N}.zip");
        try
        {
            Directory.CreateDirectory(staging);
            await DownloadAndExtractAsync(
                WindowsCli, "Ollama CLI", archive, staging, progress, cancellationToken);
            if (RequiresAmdPackage())
            {
                await DownloadAndExtractAsync(
                    WindowsAmd, "Ollama AMD support", archive, staging, progress, cancellationToken);
            }

            var executable = Directory.EnumerateFiles(
                staging, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("The Ollama CLI package is invalid.");
            var relativeExecutable = Path.GetRelativePath(staging, executable);
            File.WriteAllText(Path.Combine(staging, PortableMarker), string.Empty);
            if (Directory.Exists(PortableDirectory))
            {
                if (!File.Exists(Path.Combine(PortableDirectory, PortableMarker)))
                {
                    throw new InvalidOperationException(
                        $"The Ollama CLI folder is not owned by this app: {PortableDirectory}");
                }

                Directory.Delete(PortableDirectory, recursive: true);
            }

            Directory.Move(staging, PortableDirectory);
            ExecutablePath = Path.Combine(PortableDirectory, relativeExecutable);
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

    private static async Task DownloadAndExtractAsync(
        Uri package,
        string name,
        string archive,
        string destination,
        IProgress<OllamaProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(archive))
        {
            File.Delete(archive);
        }

        progress?.Report(new OllamaProgress($"Downloading {name}", 0, true));
        using var download = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await download.GetAsync(
            package, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Ollama download was not secure.");
        }

        var total = response.Content.Headers.ContentLength;
        if (total is < 1_000_000 or > 3_000_000_000)
        {
            throw new InvalidDataException("The Ollama download size is invalid.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(
            archive, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1_048_576, FileOptions.Asynchronous))
        {
            var buffer = new byte[1_048_576];
            long received = 0;
            var lastReported = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (received > 3_000_000_000)
                {
                    throw new InvalidDataException("The Ollama download is too large.");
                }

                if (total is > 0)
                {
                    var percent = (int)(received * 100 / total.Value);
                    if (percent > lastReported)
                    {
                        lastReported = percent;
                        progress?.Report(new OllamaProgress($"Downloading {name}", percent));
                    }
                }
            }
        }

        await ExtractAsync(archive, destination, name, progress, cancellationToken);
        File.Delete(archive);
    }

    private static async Task ExtractAsync(
        string archive,
        string destination,
        string name,
        IProgress<OllamaProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OllamaProgress($"Installing {name}", 0, true));
        using var package = ZipFile.OpenRead(archive);
        var total = package.Entries.Sum(entry => entry.Length);
        if (total is <= 0 or > 20_000_000_000)
        {
            throw new InvalidDataException("The Ollama package contents are invalid.");
        }

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long extracted = 0;
        var lastReported = 0;
        var buffer = new byte[1_048_576];
        foreach (var entry in package.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The Ollama package contains an invalid path.");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None,
                1_048_576, FileOptions.Asynchronous);
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                extracted += read;
                var percent = (int)(extracted * 100 / total);
                if (percent > lastReported)
                {
                    lastReported = percent;
                    progress?.Report(new OllamaProgress($"Installing {name}", percent));
                }
            }
        }
    }

    private static bool RequiresAmdPackage()
    {
        if (!string.IsNullOrWhiteSpace(Run(
            "nvidia-smi.exe", "--query-gpu=name", "--format=csv,noheader")))
        {
            return false;
        }

        var controllers = Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue).Name");
        return controllers?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true ||
            controllers?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static IReadOnlyList<string> RecommendModels(double vramGb) =>
        vramGb >= 8
            ? ["qwen3:8b", "qwen3:4b", "llama3.2:3b"]
            : vramGb >= 4
                ? ["llama3.2:3b", "qwen3:4b"]
                : ["llama3.2:1b"];

    public static IReadOnlyList<string> RecommendModelsForThisPc() => RecommendModels(DetectVramGb());

    private static double DetectVramGb()
    {
        var output = Run("nvidia-smi.exe",
            "--query-gpu=memory.total", "--format=csv,noheader,nounits");
        var megabytes = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var size)
                ? size
                : 0)
            .DefaultIfEmpty()
            .Max() ?? 0;
        if (megabytes > 0)
        {
            return megabytes / 1024;
        }

        output = Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Measure-Object -Property AdapterRAM -Maximum).Maximum");
        return double.TryParse(output?.Trim(), CultureInfo.InvariantCulture, out var bytes)
            ? bytes / 1_073_741_824D
            : 0;
    }

    private static string? Run(string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName)
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

    public IReadOnlyList<string> GetDownloadedModels(string? modelDirectory = null)
    {
        modelDirectory ??= Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        modelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models")
            : Path.GetFullPath(modelDirectory);
        var manifests = Path.Combine(modelDirectory, "manifests");
        if (!Directory.Exists(manifests))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(manifests, "*", SearchOption.AllDirectories)
                .Select(path => ModelNameFromManifest(manifests, path))
                .Where(name => name is not null)
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ModelNameFromManifest(string manifests, string path)
    {
        var parts = Path.GetRelativePath(manifests, path).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return null;
        }

        var name = parts[1..^1];
        if (name[0].Equals("library", StringComparison.OrdinalIgnoreCase))
        {
            name = name[1..];
        }

        return name.Length == 0 ? null : $"{string.Join('/', name)}:{parts[^1]}";
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task EnsureServerAsync(CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken))
        {
            return;
        }

        var executable = ExecutablePath ?? FindExecutable()
            ?? throw new FileNotFoundException("Ollama is not installed.");
        _ownedServer = Process.Start(new ProcessStartInfo(executable, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)
        }) ?? throw new InvalidOperationException("Ollama did not start.");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken);
            if (await IsReadyAsync(cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException("Ollama did not become ready within 60 seconds.");
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureServerAsync(cancellationToken);
        using var response = await _http.GetAsync("api/tags", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("models", out var models))
        {
            return [];
        }

        return models.EnumerateArray()
            .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task PullModelAsync(
        string model, IProgress<OllamaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath ?? FindExecutable()
            ?? throw new FileNotFoundException("Ollama is not installed.");
        await EnsureServerAsync(cancellationToken);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            }
        };
        process.StartInfo.ArgumentList.Add("pull");
        process.StartInfo.ArgumentList.Add(model.Trim());
        progress?.Report(new OllamaProgress($"Downloading {model}", 0, true));
        process.OutputDataReceived += (_, eventArgs) => ReportPullProgress(model, eventArgs.Data, progress);
        process.ErrorDataReceived += (_, eventArgs) => ReportPullProgress(model, eventArgs.Data, progress);
        if (!process.Start())
        {
            throw new InvalidOperationException("Ollama could not start the model download.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Ollama exited with code {process.ExitCode}.");
        }

        progress?.Report(new OllamaProgress($"Downloaded {model}", 100, true));
    }

    private static void ReportPullProgress(
        string model,
        string? output,
        IProgress<OllamaProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var percent = GetProgressPercent(output);
        progress?.Report(percent is null
            ? new OllamaProgress(output.Trim(), null, true)
            : new OllamaProgress($"Downloading {model}", percent));
    }

    public static int? GetProgressPercent(string output)
    {
        var marker = output.LastIndexOf('%');
        if (marker < 1)
        {
            return null;
        }

        var start = marker - 1;
        while (start > 0 && char.IsDigit(output[start - 1]))
        {
            start--;
        }

        return int.TryParse(output.AsSpan(start, marker - start), out var percent) && percent <= 100
            ? percent
            : null;
    }

    public async Task UnloadModelAsync(string model)
    {
        if (!await IsReadyAsync() || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync("api/generate", new
            {
                model = model.Trim(),
                keep_alive = 0
            });
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopOwnedServerAsync();
        _http.Dispose();
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

        foreach (var name in new[] { "ollama", "llama-server" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && IsInsidePortableDirectory(path))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5_000);
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

        Directory.Delete(PortableDirectory, recursive: true);
        var parent = Path.GetDirectoryName(PortableDirectory)!;
        if (!Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }

        if (ExecutablePath is not null && IsInsidePortableDirectory(ExecutablePath))
        {
            ExecutablePath = null;
        }
    }

    private static bool IsInsidePortableDirectory(string path)
    {
        var relative = Path.GetRelativePath(PortableDirectory, Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
