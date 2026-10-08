using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ProjectRemnants.Setup.Voice;

public sealed record VoiceProgress(string Message, int? Percent = null);

public sealed record VoiceStatus(
    bool EngineInstalled, IReadOnlyList<string> InstalledModelFiles, string? ActiveModelFile, string Root);

public sealed class VoiceInstaller
{
    private const string Marker = ".project-remnants-portable";
    private const string ConfigFileName = "speech.properties";

    public static string ServerName =>
        OperatingSystem.IsWindows() ? "whisper-server.exe" : "whisper-server";

    private readonly string _root;
    private readonly HttpClient _http;

    public VoiceInstaller(string? root = null, HttpClient? http = null)
    {
        _root = root ?? DefaultRoot;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectRemnants", "Whisper");

    public string Root => _root;

    public VoiceStatus GetStatus()
    {
        var engine = File.Exists(Path.Combine(_root, "bin", ServerName));
        var models = new List<string>();
        var modelDirectory = Path.Combine(_root, "models");
        if (Directory.Exists(modelDirectory))
        {
            models.AddRange(Directory.EnumerateFiles(modelDirectory, "ggml-*.bin")
                .Select(path => Path.GetFileName(path))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        }

        return new VoiceStatus(engine, models, ReadActiveModel(), _root);
    }

    public async Task InstallAsync(
        VoiceModel model,
        IProgress<VoiceProgress>? progress = null,
        CancellationToken cancellationToken = default,
        VoiceAsset? engine = null)
    {
        EnsureOwned();
        EnsureServerStopped();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, Marker), string.Empty);

        var modelDirectory = Path.Combine(_root, "models");
        Directory.CreateDirectory(modelDirectory);
        var modelPath = Path.Combine(modelDirectory, model.Asset.FileName);
        if (!File.Exists(modelPath) || !await MatchesHashAsync(modelPath, model.Asset, cancellationToken))
        {
            var partial = modelPath + ".part";
            await DownloadAsync(model.Asset, $"Downloading {model.Title}", partial, progress, cancellationToken);
            File.Move(partial, modelPath, overwrite: true);
        }

        var engineAsset = engine ?? VoiceCatalog.Engine;
        if (!File.Exists(Path.Combine(_root, "bin", ServerName)))
        {
            await InstallEngineAsync(engineAsset, progress, cancellationToken);
        }

        SelectModel(model);
        progress?.Report(new VoiceProgress("Voice input is ready.", 100));
    }

    public void SelectModel(VoiceModel model)
    {
        EnsureOwned();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ConfigFileName);
        List<string> lines = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : new List<string>();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("model=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"model={model.Asset.FileName}";
                replaced = true;
            }
        }

        if (!replaced)
        {
            lines.Add($"model={model.Asset.FileName}");
        }

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Remove()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        EnsureOwned();
        EnsureServerStopped();
        Directory.Delete(_root, recursive: true);
    }

    private async Task InstallEngineAsync(
        VoiceAsset engine, IProgress<VoiceProgress>? progress, CancellationToken cancellationToken)
    {
        var extension = engine.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            engine.FileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                ? ".tar.gz"
                : Path.GetExtension(engine.FileName);
        var archive = Path.Combine(Path.GetTempPath(), $"whisper-engine-{Guid.NewGuid():N}{extension}");
        var staging = Path.Combine(_root, $".bin-{Guid.NewGuid():N}");
        try
        {
            await DownloadAsync(engine, "Downloading the speech engine", archive, progress, cancellationToken);
            Directory.CreateDirectory(staging);
            progress?.Report(new VoiceProgress("Installing the speech engine"));
            if (extension.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarGzAsync(archive, staging, cancellationToken);
                TrimPayload(staging);
            }
            else
            {
                ExtractEngine(archive, staging);
            }

            var server = Directory.EnumerateFiles(staging, ServerName, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException(
                    $"The speech engine package does not contain {ServerName}.");
            var payloadDirectory = Path.GetDirectoryName(server)!;

            var target = Path.Combine(_root, "bin");
            var previous = Path.Combine(_root, $".bin-old-{Guid.NewGuid():N}");
            if (Directory.Exists(target))
            {
                Directory.Move(target, previous);
            }

            if (string.Equals(
                    Path.GetFullPath(payloadDirectory), Path.GetFullPath(staging),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                Directory.Move(staging, target);
            }
            else
            {
                Directory.Move(payloadDirectory, target);
            }

            var serverPath = Path.Combine(target, ServerName);
            if (!OperatingSystem.IsWindows() && File.Exists(serverPath))
            {
                try
                {
                    File.SetUnixFileMode(serverPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception)
                {
                }
            }

            if (Directory.Exists(previous))
            {
                Directory.Delete(previous, recursive: true);
            }
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

    private static bool WantedEngineFile(string name)
    {
        if (name.Equals(ServerName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("SDL2", StringComparison.OrdinalIgnoreCase);
        }

        return name.StartsWith("lib", StringComparison.Ordinal)
            && name.Contains(".so", StringComparison.Ordinal)
            && !name.StartsWith("libSDL2", StringComparison.Ordinal);
    }

    private static void ExtractEngine(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (name.Length == 0 || entry.Length > 50_000_000)
            {
                continue;
            }

            if (!WantedEngineFile(name))
            {
                continue;
            }

            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
        }
    }

    private static void TrimPayload(string staging)
    {
        foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            if (!WantedEngineFile(Path.GetFileName(path)))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task ExtractTarGzAsync(
        string archive, string destination, CancellationToken cancellationToken)
    {
        await using (var file = File.OpenRead(archive))
        await using (var gzip = new System.IO.Compression.GZipStream(
            file, System.IO.Compression.CompressionMode.Decompress))
        {
            ValidateTarEntries(gzip, cancellationToken);
        }

        await using (var file = File.OpenRead(archive))
        await using (var gzip = new System.IO.Compression.GZipStream(
            file, System.IO.Compression.CompressionMode.Decompress))
        {
            System.Formats.Tar.TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
        }
    }

    public static void ValidateTarEntries(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new System.Formats.Tar.TarReader(stream, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsUnsafePath(entry.Name))
            {
                throw new InvalidDataException("The package contains an invalid path.");
            }

            // .so version links (e.g. libwhisper.so.1 -> libwhisper.so.1.9.4)
            // are normal; only reject links that escape the package.
            if (entry.EntryType is System.Formats.Tar.TarEntryType.SymbolicLink
                or System.Formats.Tar.TarEntryType.HardLink &&
                (string.IsNullOrEmpty(entry.LinkName) || IsUnsafePath(entry.LinkName)))
            {
                throw new InvalidDataException("The package contains an invalid link.");
            }
        }
    }

    private static bool IsUnsafePath(string name) =>
        Path.IsPathRooted(name) || name.Split('/').Any(part => part == "..");

    private async Task DownloadAsync(
        VoiceAsset asset,
        string label,
        string destination,
        IProgress<VoiceProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        progress?.Report(new VoiceProgress(label, 0));
        using var response = await _http.GetAsync(
            asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The download was not secure.");
        }

        var length = response.Content.Headers.ContentLength;
        if (length is not null && length != asset.Size)
        {
            throw new InvalidDataException($"{asset.FileName} has an unexpected size.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1_048_576, FileOptions.Asynchronous))
            {
                var buffer = new byte[1_048_576];
                long received = 0;
                var lastPercent = -1;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > asset.Size)
                    {
                        throw new InvalidDataException($"{asset.FileName} is larger than expected.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    var percent = (int)(received * 100 / asset.Size);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(new VoiceProgress(label, percent));
                    }
                }

                if (received != asset.Size)
                {
                    throw new InvalidDataException($"{asset.FileName} was incomplete.");
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{asset.FileName} failed its integrity check.");
            }
        }
        catch
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            throw;
        }
    }

    private static async Task<bool> MatchesHashAsync(
        string path, VoiceAsset asset, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != asset.Size)
        {
            return false;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private string? ReadActiveModel()
    {
        var path = Path.Combine(_root, ConfigFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("model=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["model=".Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private void EnsureOwned()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        if (File.Exists(Path.Combine(_root, Marker)))
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(_root).Any())
        {
            return;
        }

        throw new InvalidOperationException(
            $"The voice folder is not managed by this app: {_root}");
    }

    private void EnsureServerStopped()
    {
        var server = Path.Combine(_root, "bin", ServerName);
        foreach (var process in Process.GetProcessesByName("whisper-server"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && string.Equals(
                    path, server, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The speech engine is running. Close Project Zomboid and try again.");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
