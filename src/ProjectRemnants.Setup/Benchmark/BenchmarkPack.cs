using System.Reflection;
using System.Text.Json;

namespace ProjectRemnants.Setup.Benchmark;

public sealed record BenchmarkCase(
    string Id, string Name, string Utterance, string[] Tags, JsonElement Expect,
    JsonElement Fixture, JsonElement Request, string JavaBypass);

public sealed record BenchmarkPack(string Version, string SourceSha256, IReadOnlyList<BenchmarkCase> Cases)
{
    public static BenchmarkPack Load()
    {
        var assembly = typeof(BenchmarkPack).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("forced-model-pack-v1.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var cases = root.GetProperty("cases").EnumerateArray().Select(item => new BenchmarkCase(
            item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!,
            item.GetProperty("utterance").GetString()!,
            item.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray(),
            item.GetProperty("expect").Clone(), item.GetProperty("fixture").Clone(),
            item.GetProperty("request").Clone(), item.GetProperty("java_bypass").GetString()!)).ToArray();
        if (cases.Length != 223 || cases.Select(item => item.Id).Distinct().Count() != 223 ||
            cases.Count(item => item.JavaBypass == "status") != 1 ||
            cases.Count(item => item.JavaBypass == "stub") != 2 ||
            cases.Any(item => item.Request.GetProperty("messages").GetArrayLength() == 0))
        {
            throw new InvalidDataException("The bundled forced-model test pack is incomplete.");
        }
        return new BenchmarkPack(root.GetProperty("version").GetString()!,
            root.GetProperty("source_sha256").GetString()!, cases);
    }
}
