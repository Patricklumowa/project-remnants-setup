using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectRemnants.Setup.Llm;

public sealed record CatalogTag(string Tag, double ParametersB);

public sealed record CatalogModel(
    string Name,
    string Description,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<CatalogTag> Tags)
{
    public bool IsCloud => Capabilities.Contains("cloud", StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> DownloadNames =>
        Tags.Count == 0 ? [Name] : Tags.Select(tag => $"{Name}:{tag.Tag}");
}

public static class OllamaCatalog
{
    private static readonly Uri Library = new("https://ollama.com/library");
    private static readonly Regex BlockPattern = new(
        "(?s)<li\\s+class=\"flex items-baseline[^\"]*\"[^>]*>(?<body>.*?)</li>",
        RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = new(
        "href=\"/library/(?<name>[a-zA-Z0-9._-]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex DescriptionPattern = new(
        "(?s)<p class=\"max-w-lg[^\"]*\">(?<text>.*?)</p>", RegexOptions.CultureInvariant);
    private static readonly Regex SpanPattern = new(
        "<span\\s+class=\"[^\"]*\"\\s*>(?<text>[^<]*)</span>", RegexOptions.CultureInvariant);
    private static readonly Regex SizePattern = new(
        "^(?:[0-9]+(?:\\.[0-9]+)?[bm]|[0-9]+x[0-9]+(?:\\.[0-9]+)?b)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Capabilities = new(
        StringComparer.OrdinalIgnoreCase) { "tools", "thinking", "vision", "cloud", "embedding", "audio" };
    private static readonly HashSet<string> Ignorable = new(
        StringComparer.OrdinalIgnoreCase) { "updated", "pulls", "tags", "tag", "new", "library" };

    public static async Task<IReadOnlyList<CatalogModel>> FetchAsync(
        HttpClient http, CancellationToken cancellationToken = default)
    {
        var html = await http.GetStringAsync(Library, cancellationToken);
        return Parse(html);
    }

    public static IReadOnlyList<CatalogModel> Parse(string html)
    {
        var models = new List<CatalogModel>();
        if (string.IsNullOrEmpty(html))
        {
            return models;
        }

        foreach (Match block in BlockPattern.Matches(html))
        {
            var body = block.Groups["body"].Value;
            var nameMatch = NamePattern.Match(body);
            if (!nameMatch.Success)
            {
                continue;
            }

            var name = nameMatch.Groups["name"].Value;
            if (!models.TrueForAll(model => !model.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var description = WebUtility.HtmlDecode(
                DescriptionPattern.Match(body).Groups["text"].Value).Trim();

            var capabilities = new List<string>();
            var tags = new List<CatalogTag>();
            foreach (Match span in SpanPattern.Matches(body))
            {
                var text = WebUtility.HtmlDecode(span.Groups["text"].Value).Trim();
                if (text.Length == 0 || Ignorable.Contains(text))
                {
                    continue;
                }

                if (Capabilities.Contains(text))
                {
                    capabilities.Add(text.ToLowerInvariant());
                }
                else if (SizePattern.IsMatch(text) &&
                    ModelFit.TryParseParametersB(text, out var billions))
                {
                    tags.Add(new CatalogTag(text.ToLowerInvariant(), billions));
                }
            }

            tags.Sort((left, right) => left.ParametersB.CompareTo(right.ParametersB));
            models.Add(new CatalogModel(name, description, capabilities, tags));
        }

        models.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return models;
    }

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectRemnants", "OllamaCatalog.json");

    public static async Task<IReadOnlyList<CatalogModel>> LoadAsync(
        HttpClient http, CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await FetchAsync(http, cancellationToken);
            if (models.Count != 0)
            {
                await SaveCacheAsync(models, cancellationToken);
            }

            return models;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await LoadCacheAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<CatalogModel>> LoadCacheAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return [];
            }

            var json = await File.ReadAllTextAsync(CachePath, cancellationToken);
            return JsonSerializer.Deserialize<List<CatalogModel>>(json) ?? [];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private static async Task SaveCacheAsync(
        IReadOnlyList<CatalogModel> models, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temporary = CachePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = false }),
                    cancellationToken);
                File.Move(temporary, CachePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static string FormatSize(double gigabytes) =>
        gigabytes <= 0
            ? "?"
            : gigabytes.ToString(gigabytes >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";
}
