using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectRemnants.Setup.Benchmark;

public sealed class BenchmarkCaseResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Utterance { get; init; }
    public required string[] Tags { get; init; }
    public required string JavaBypass { get; init; }
    public required JsonElement ExpectedAction { get; init; }
    public required JsonElement Request { get; init; }
    public JsonElement? ActualAction { get; set; }
    public string RawResponse { get; set; } = "";
    public string RawOllamaResponse { get; set; } = "";
    public string Status { get; set; } = "FAIL";
    public List<string> FailureReasons { get; } = [];
    public string[] UnscoredJavaAssertions { get; init; } = [];
    public double? ResponseMs { get; set; }
    public double? LoadMs { get; set; }
    public long? PromptTokens { get; set; }
    public long? CachedPromptTokens { get; set; }
    public long? PromptEvalNs { get; set; }
    public long? GenerationTokens { get; set; }
    public long? GenerationEvalNs { get; set; }
    public double? PromptTokensPerSecond => PromptEvalNs > 0 && PromptTokens is >= 0
        ? Math.Max(0, PromptTokens.Value - (CachedPromptTokens ?? 0)) * 1e9 / PromptEvalNs.Value : null;
    public double? GenerationTokensPerSecond => GenerationEvalNs > 0 && GenerationTokens is >= 0
        ? GenerationTokens.Value * 1e9 / GenerationEvalNs.Value : null;
}

public sealed record BenchmarkCategoryScore(int Passed, int Total);

public sealed class BenchmarkSummary
{
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int CancelledCases { get; init; }
    public required int Remaining { get; init; }
    public required Dictionary<string, BenchmarkCategoryScore> Categories { get; init; }
    public double? CorrectnessPercent => Passed + Failed == 0 ? null : 100.0 * Passed / (Passed + Failed);
    public double? MedianResponseMs { get; init; }
    public double? P90ResponseMs { get; init; }
    public double? PromptTokensPerSecond { get; init; }
    public double? GenerationTokensPerSecond { get; init; }
}

public sealed class BenchmarkReport
{
    public string Kind { get; } = "forced-model benchmark";
    public string ScoringMethod { get; } =
        "Each case sends one production-shaped Ollama chat request. A strict production JSON envelope and the expected action type and explicit action fields are scored. Correctness and speed are separate.";
    public string JavaBenchmarkDifferences { get; } =
        "The Java --production --only-model run lists 223 cases but calls the model for 220: one status route and two stub replies bypass it. This pack calls the model for all 223. Java additionally repairs and normalizes replies, may retry them, and scores item/storage resolution and simulated execution; those checks are not included in this action extraction score.";
    public required string ModelTag { get; init; }
    public required string TestPackVersion { get; init; }
    public required string TestPackSourceSha256 { get; init; }
    public required int ContextSetting { get; init; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public string Quantization { get; set; } = "not reported";
    public string Placement { get; set; } = "not reported";
    public long? ModelSizeBytes { get; set; }
    public long? GpuBytes { get; set; }
    public double? ColdLoadMs { get; set; }
    public bool Cancelled { get; set; }
    public required int TotalCases { get; init; }
    public string AutoSavePath { get; init; } = "";
    public List<string> Warnings { get; } = [];
    public List<BenchmarkCaseResult> Cases { get; } = [];

    public BenchmarkSummary Summary
    {
        get
        {
            var scored = Cases.Where(item => item.Status is "PASS" or "FAIL").ToArray();
            var times = Cases.Where(item => item.ResponseMs.HasValue)
                .Select(item => item.ResponseMs!.Value).Order().ToArray();
            var categories = scored.SelectMany(item => item.Tags.Distinct().Select(tag => (tag, item)))
                .GroupBy(pair => pair.tag)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new BenchmarkCategoryScore(
                    group.Count(pair => pair.item.Status == "PASS"), group.Count()));
            return new BenchmarkSummary
            {
                Passed = scored.Count(item => item.Status == "PASS"),
                Failed = scored.Count(item => item.Status == "FAIL"),
                CancelledCases = Cases.Count(item => item.Status == "CANCELLED"),
                Remaining = TotalCases - Cases.Count,
                Categories = categories,
                MedianResponseMs = times.Length == 0 ? null : times.Length % 2 == 1
                    ? times[times.Length / 2] : (times[times.Length / 2 - 1] + times[times.Length / 2]) / 2,
                P90ResponseMs = times.Length == 0 ? null : times[(int)Math.Ceiling(times.Length * .9) - 1],
                PromptTokensPerSecond = TokensPerSecond(Cases, prompt: true),
                GenerationTokensPerSecond = TokensPerSecond(Cases, prompt: false)
            };
        }
    }

    private static double? TokensPerSecond(IEnumerable<BenchmarkCaseResult> cases, bool prompt)
    {
        var measured = cases.Where(item => prompt ? item.PromptEvalNs > 0 && item.PromptTokens.HasValue
            : item.GenerationEvalNs > 0 && item.GenerationTokens.HasValue).ToArray();
        if (measured.Length == 0) return null;
        var tokens = prompt ? measured.Sum(item => Math.Max(0,
            item.PromptTokens!.Value - (item.CachedPromptTokens ?? 0)))
            : measured.Sum(item => item.GenerationTokens!.Value);
        var duration = measured.Sum(item => prompt ? item.PromptEvalNs!.Value : item.GenerationEvalNs!.Value);
        return tokens * 1e9 / duration;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }),
            Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class OllamaBenchmark : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _caseTimeout;
    private readonly string _reportDirectory;

    public OllamaBenchmark(HttpClient? http = null, TimeSpan? caseTimeout = null,
        string? reportDirectory = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _caseTimeout = caseTimeout ?? TimeSpan.FromMinutes(3);
        _reportDirectory = reportDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectRemnants", "Benchmarks");
    }

    public async Task<BenchmarkReport> RunAsync(BenchmarkPack pack, string model,
        IProgress<BenchmarkReport>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Select a local model first.", nameof(model));
        var context = pack.Cases[0].Request.GetProperty("options").GetProperty("num_ctx").GetInt32();
        var report = new BenchmarkReport
        {
            ModelTag = model.Trim(), TestPackVersion = pack.Version,
            TestPackSourceSha256 = pack.SourceSha256, ContextSetting = context,
            TotalCases = pack.Cases.Count,
            AutoSavePath = Path.Combine(_reportDirectory,
                $"forced-model-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json")
        };
        report.Save(report.AutoSavePath);
        progress?.Report(report);
        try
        {
            await ReadQuantizationAsync(report, cancellationToken);
            var coldReady = await UnloadAsync(report, cancellationToken);
            var placementRead = false;
            foreach (var item in pack.Cases)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var result = await RunCaseAsync(item, model.Trim(), cancellationToken);
                report.Cases.Add(result);
                if (coldReady && report.ColdLoadMs is null) report.ColdLoadMs = result.LoadMs;
                if (!placementRead && result.ResponseMs.HasValue)
                {
                    await ReadPlacementAsync(report, cancellationToken);
                    placementRead = true;
                }
                report.Save(report.AutoSavePath);
                progress?.Report(report);
                if (result.Status == "CANCELLED") break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The cases already written to the auto-saved report remain available.
        }
        finally
        {
            report.Cancelled = cancellationToken.IsCancellationRequested;
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Save(report.AutoSavePath);
            progress?.Report(report);
        }
        return report;
    }

    private async Task<BenchmarkCaseResult> RunCaseAsync(BenchmarkCase item, string model,
        CancellationToken cancellationToken)
    {
        var request = JsonNode.Parse(item.Request.GetRawText())!.AsObject();
        request["model"] = model;
        var requestJson = request.ToJsonString();
        using var requestDocument = JsonDocument.Parse(requestJson);
        var result = new BenchmarkCaseResult
        {
            Id = item.Id, Name = item.Name, Utterance = item.Utterance, Tags = item.Tags,
            JavaBypass = item.JavaBypass, ExpectedAction = item.Expect, Request = requestDocument.RootElement.Clone(),
            UnscoredJavaAssertions = new[] { "execution", "item_types", "storage" }
                .Where(name => item.Expect.TryGetProperty(name, out _)).ToArray()
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_caseTimeout);
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await _http.PostAsync("api/chat",
                new StringContent(requestJson, Encoding.UTF8, "application/json"), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            timer.Stop();
            result.RawOllamaResponse = body;
            if (!response.IsSuccessStatusCode)
            {
                result.FailureReasons.Add($"Ollama HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}");
                return result;
            }
            result.ResponseMs = timer.Elapsed.TotalMilliseconds;
            using var reply = JsonDocument.Parse(body);
            var root = reply.RootElement;
            if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
                throw new JsonException("Ollama response did not finish.");
            result.LoadMs = Milliseconds(root, "load_duration");
            result.PromptTokens = Number(root, "prompt_eval_count");
            result.CachedPromptTokens = Number(root, "prompt_eval_cached_count");
            result.PromptEvalNs = Number(root, "prompt_eval_duration");
            result.GenerationTokens = Number(root, "eval_count");
            result.GenerationEvalNs = Number(root, "eval_duration");
            result.RawResponse = root.GetProperty("message").GetProperty("content").GetString() ?? "";
            ScoreAction(item.Expect, result);
            result.Status = result.FailureReasons.Count == 0 ? "PASS" : "FAIL";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Status = "CANCELLED";
            result.FailureReasons.Add("Cancelled by user during Ollama request.");
        }
        catch (OperationCanceledException)
        {
            result.FailureReasons.Add($"Ollama request timed out after {_caseTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception)
        {
            result.FailureReasons.Add($"{exception.GetType().Name}: {exception.Message}");
        }
        return result;
    }

    public static void ScoreAction(JsonElement expected, BenchmarkCaseResult result)
    {
        var raw = result.RawResponse.Trim();
        if (raw.Length > 4096) throw new JsonException("Action reply exceeds the production 4096-character limit.");
        if (raw.StartsWith("```", StringComparison.Ordinal))
            raw = Regex.Replace(raw, "^```(?:json)?\\s*|\\s*```$", "", RegexOptions.IgnoreCase).Trim();
        using var parsed = JsonDocument.Parse(raw);
        var envelope = parsed.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("dialogue", out var dialogue) || dialogue.ValueKind != JsonValueKind.String ||
            !envelope.TryGetProperty("relationship", out var relationship) ||
            relationship.ValueKind != JsonValueKind.String ||
            relationship.GetString() is not ("SKIP" or "UPDATE") ||
            envelope.EnumerateObject().Count() != 3)
            throw new JsonException("Expected a production reply with dialogue, relationship and action.");
        result.ActualAction = action.Clone();
        var fields = new[] { "type", "mode", "actor", "target", "item", "source", "destination",
            "location", "quantity", "all", "deliveries" };
        if (action.EnumerateObject().Count() != fields.Length ||
            fields.Any(field => !action.TryGetProperty(field, out _)) ||
            fields.Take(8).Any(field => action.GetProperty(field).ValueKind != JsonValueKind.String) ||
            action.GetProperty("quantity").ValueKind != JsonValueKind.Number ||
            !action.GetProperty("quantity").TryGetInt32(out _) ||
            action.GetProperty("all").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            action.GetProperty("deliveries").ValueKind != JsonValueKind.Array)
            throw new JsonException("Action does not match the production JSON field contract.");
        var wantedType = expected.GetProperty("type").GetString()!;
        var actualType = Word(action, "type").ToUpperInvariant();
        if (actualType != wantedType)
        {
            result.FailureReasons.Add($"Action type: expected {wantedType}, got {actualType}.");
            return;
        }
        foreach (var field in new[] { "mode", "actor", "target", "item", "source", "destination", "location" })
        {
            if (!expected.TryGetProperty(field, out var value)) continue;
            var wanted = Normalize(value.GetString()!, field);
            var actual = Normalize(Word(action, field), field);
            if (field == "target" && wanted is "" or "me" or "myself" or "player" or "speaker" &&
                actual is "" or "me" or "myself" or "player" or "speaker") continue;
            if (wanted != actual) result.FailureReasons.Add(
                $"{field}: expected '{value.GetString()}', got '{Word(action, field)}'.");
        }
        if (expected.TryGetProperty("quantity", out var quantity) &&
            (!action.TryGetProperty("quantity", out var actualQuantity) ||
             actualQuantity.ValueKind != JsonValueKind.Number || actualQuantity.GetInt32() != quantity.GetInt32()))
            result.FailureReasons.Add($"quantity: expected {quantity}, got {Word(action, "quantity")}.");
        if (expected.TryGetProperty("all", out var all) &&
            (!action.TryGetProperty("all", out var actualAll) || actualAll.ValueKind != all.ValueKind))
            result.FailureReasons.Add($"all: expected {all}, got {Word(action, "all")}.");
    }

    private static string Word(JsonElement item, string field) => item.TryGetProperty(field, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";

    private static string Normalize(string text, string field)
    {
        var normalized = Regex.Replace(Regex.Replace(text, "([a-z])([A-Z])", "$1 $2"),
            "[^\\p{L}\\p{N}]+", " ").Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, "\\s+", " ");
        if (field == "item")
        {
            normalized = Regex.Replace(normalized, "^(this|that|these|those|loose|my|your|a|an|the) ", "");
            normalized = Regex.Replace(normalized, " (gun|firearm|weapon)$", "");
        }
        else if (field is "target" or "source" or "destination" or "location")
            normalized = Regex.Replace(normalized, "^(this|that|the|these|those) ", "");
        return normalized;
    }

    private async Task ReadQuantizationAsync(BenchmarkReport report, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.PostAsJsonAsync("api/show", new { model = report.ModelTag }, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            if (document.RootElement.TryGetProperty("details", out var details) &&
                details.TryGetProperty("quantization_level", out var quantization))
                report.Quantization = quantization.GetString() ?? "not reported";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { report.Warnings.Add("Quantization unavailable: " + exception.Message); }
    }

    private async Task<bool> UnloadAsync(BenchmarkReport report, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.PostAsJsonAsync("api/generate",
                new { model = report.ModelTag, keep_alive = 0 }, timeout.Token);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            report.Warnings.Add("Cold-load reset failed: " + exception.Message);
            return false;
        }
    }

    private async Task ReadPlacementAsync(BenchmarkReport report, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.GetAsync("api/ps", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var active = document.RootElement.GetProperty("models").EnumerateArray().FirstOrDefault(item =>
                string.Equals(Word(item, "name"), report.ModelTag, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Word(item, "model"), report.ModelTag, StringComparison.OrdinalIgnoreCase));
            if (active.ValueKind != JsonValueKind.Object) return;
            var size = Number(active, "size");
            var gpu = Number(active, "size_vram");
            if (size is null || gpu is null || size == 0) return;
            report.ModelSizeBytes = size;
            report.GpuBytes = gpu;
            report.Placement = gpu == 0 ? "CPU" : gpu >= size ? "GPU" :
                $"Mixed GPU/CPU ({100.0 * gpu / size:0.#}% of reported model bytes in VRAM)";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { report.Warnings.Add("GPU/CPU placement unavailable: " + exception.Message); }
    }

    private static long? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) ? number : null;
    private static double? Milliseconds(JsonElement item, string name) => Number(item, name) is long ns
        ? ns / 1e6 : null;

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
