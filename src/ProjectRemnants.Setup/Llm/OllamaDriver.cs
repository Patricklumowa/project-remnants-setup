using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectRemnants.Setup.Llm;

public sealed class OllamaDriver : IAsyncDisposable
{
    private static readonly Uri Server = new("http://127.0.0.1:11434/");
    private readonly HttpClient _http = new() { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(5) };
    private Process? _ownedServer;

    public string? ExecutablePath { get; private set; }
    public bool OwnsServer => _ownedServer is { HasExited: false };

    public string? FindExecutable()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim().Trim('"'), "ollama.exe")));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe"));
        ExecutablePath = candidates.FirstOrDefault(File.Exists);
        return ExecutablePath;
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
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Ollama did not start.");

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (await IsReadyAsync(cancellationToken))
            {
                return;
            }

            if (_ownedServer.HasExited)
            {
                break;
            }
        }

        throw new InvalidOperationException("Ollama did not become ready.");
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
        string model, IProgress<string>? progress = null,
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
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("pull");
        process.StartInfo.ArgumentList.Add(model.Trim());
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                progress?.Report(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                progress?.Report(eventArgs.Data);
            }
        };
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
        _http.Dispose();
        if (_ownedServer is null)
        {
            return;
        }

        try
        {
            if (!_ownedServer.HasExited)
            {
                _ownedServer.Kill(entireProcessTree: true);
                await _ownedServer.WaitForExitAsync();
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _ownedServer.Dispose();
        }
    }
}
