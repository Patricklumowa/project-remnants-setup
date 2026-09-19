using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectRemnants.Setup.Llm;

public sealed record OllamaProgress(string Message, int? Percent = null, bool Log = false);

public enum GpuAcceleration
{
    Unknown,
    None,
    NvidiaCuda,
    AmdRocm
}

public sealed record GpuInfo(
    string Name, string Summary, string Detail, GpuAcceleration Acceleration);

public sealed record GpuAdapter(string Name, double VramGb, bool Discrete);

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

    private static readonly (double ParametersB, string Name)[] RecommendationLadder =
    [
        (1, "llama3.2:1b"),
        (3, "llama3.2:3b"),
        (4, "qwen3:4b"),
        (8, "qwen3:8b"),
        (14, "qwen3:14b"),
        (32, "qwen3:32b"),
        (70, "llama3.1:70b")
    ];

    public static IReadOnlyList<string> RecommendModels(double vramGb)
    {
        var budget = Math.Max(vramGb, 0) * 0.88;
        var fitting = RecommendationLadder
            .Where(entry => ModelFit.EstimateDownloadGb(entry.ParametersB) <= budget)
            .ToArray();
        if (fitting.Length == 0)
        {
            return ["llama3.2:1b"];
        }

        return fitting
            .Reverse()
            .Take(3)
            .Select(entry => entry.Name)
            .ToArray();
    }

    public static IReadOnlyList<string> RecommendModelsForThisPc() => RecommendModels(DetectVramGb());

    public static string RecommendationTip(double vramGb)
    {
        if (vramGb <= 0)
        {
            return "Tip: no GPU detected. Small models like llama3.2:1b run best on the CPU.";
        }

        var recommended = RecommendModels(vramGb);
        return $"Tip: {recommended[0]} is a good match for your {vramGb:0.#} GB GPU.";
    }

    public static GpuInfo DetectPreferredGpu() => DescribePreferredGpu(QueryGpuInventory());

    public static GpuInfo DescribePreferredGpu(IReadOnlyList<GpuAdapter> graphics)
    {
        if (graphics.Count == 0)
        {
            return new GpuInfo(
                "Unknown", "No GPU detected", "Ollama will run on the CPU.", GpuAcceleration.None);
        }

        if (graphics.Count == 1)
        {
            var only = graphics[0];
            return new GpuInfo(
                only.Name,
                $"Only GPU: {only.Name}",
                $"Ollama will use {only.Name}.",
                Classify(only));
        }

        var withVram = graphics.Where(record => record.VramGb > 0).ToArray();
        if (withVram.Length == 0)
        {
            var first = graphics[0];
            return new GpuInfo(
                first.Name,
                $"Selected GPU: {first.Name}",
                "Ollama will auto-select the GPU.",
                GpuAcceleration.Unknown);
        }

        var preferred = withVram
            .OrderByDescending(record => record.Discrete)
            .ThenByDescending(record => record.VramGb)
            .First();
        var acceleration = Classify(preferred);
        var reason = acceleration == GpuAcceleration.None
            ? $"{preferred.Name} has the most video memory."
            : $"Ollama will use {preferred.Name}, the fastest GPU with the most memory.";
        return new GpuInfo(
            preferred.Name,
            $"Preferred GPU: {preferred.Name}",
            reason,
            acceleration);
    }

    public static GpuAcceleration Classify(string name, bool discrete = true)
    {
        if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("geforce", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rtx", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("quadro", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tesla", StringComparison.OrdinalIgnoreCase))
        {
            return GpuAcceleration.NvidiaCuda;
        }

        if (name.Contains("radeon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ryzen", StringComparison.OrdinalIgnoreCase))
        {
            return GpuAcceleration.AmdRocm;
        }

        return discrete ? GpuAcceleration.Unknown : GpuAcceleration.None;
    }

    private static GpuAcceleration Classify(GpuAdapter record) =>
        Classify(record.Name, record.Discrete);

    private static IReadOnlyList<GpuAdapter> QueryGpuInventory()
    {
        var records = new List<GpuAdapter>();
        var nvidia = Run("nvidia-smi.exe",
            "--query-gpu=name,memory.total", "--format=csv,noheader,nounits");
        if (!string.IsNullOrWhiteSpace(nvidia))
        {
            foreach (var line in nvidia.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(',', StringSplitOptions.TrimEntries);
                if (fields.Length != 0 && !string.IsNullOrWhiteSpace(fields[0]))
                {
                    var vramGb = fields.Length > 1 &&
                        double.TryParse(fields[1], CultureInfo.InvariantCulture, out var megabytes)
                            ? megabytes / 1024
                            : 0;
                    records.Add(new GpuAdapter(fields[0], vramGb, Discrete: true));
                }
            }
        }

        var output = Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            "(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | " +
            "Select-Object Name,AdapterRAM | ConvertTo-Json -Compress)");
        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                using var document = JsonDocument.Parse(output);
                var elements = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray()
                    : EnumerateSingle(document.RootElement);
                foreach (var element in elements)
                {
                    var name = element.TryGetProperty("Name", out var nameElement)
                        ? nameElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(name) || records.Any(record =>
                        record.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var bytes = element.TryGetProperty("AdapterRAM", out var memoryElement) &&
                        memoryElement.ValueKind == JsonValueKind.Number &&
                        memoryElement.TryGetDouble(out var value)
                            ? value
                            : 0;
                    var vramGb = bytes / 1_073_741_824D;
                    records.Add(new GpuAdapter(name, vramGb, vramGb >= 6));
                }
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }

    private static IEnumerable<JsonElement> EnumerateSingle(JsonElement element)
    {
        yield return element;
    }

    public static double DetectVramGb() => QueryGpuInventory()
        .Select(record => record.VramGb)
        .DefaultIfEmpty()
        .Max();

    public static double DetectSystemRamGb()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0 ? bytes / 1_073_741_824D : 0;
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
