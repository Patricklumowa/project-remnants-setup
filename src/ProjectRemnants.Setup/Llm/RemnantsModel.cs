using System.Globalization;

namespace ProjectRemnants.Setup.Llm;

public static class RemnantsModel
{
    public const string Title = "Remnants LLM v5";
    public const string Repository = "hf.co/PatrickPat/remnants-llm-v5-GGUF";
    public const string Quantization = "Q4_K_M";
    public const string Name = Repository + ":" + Quantization;
    public const double DownloadGb = 2.5;
    public const double ParametersB = 4;

    public const string Description =
        "Made for Project Remnants. It answers the game's orders and talks in character as your " +
        "survivors. A 4B model of about 2.5 GB, tuned on this mod's own order and dialogue examples.";

    public static bool IsRemnants(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        model.Contains("remnants", StringComparison.OrdinalIgnoreCase);

    public static Runability Fit(double vramGb, double ramGb) =>
        ModelFit.Evaluate(DownloadGb, vramGb, ramGb);

    public static IReadOnlyList<string> Recommend(double vramGb, double ramGb)
    {
        var others = OllamaDriver.RecommendModels(vramGb);
        return Fit(vramGb, ramGb) is Runability.Gpu or Runability.CpuOnly
            ? [Name, .. others]
            : others;
    }

    public static IReadOnlyList<string> RecommendForThisPc() =>
        Recommend(OllamaDriver.DetectVramGb(), OllamaDriver.DetectSystemRamGb());

    public static IReadOnlyList<string> WithRecommendation(IReadOnlyList<string> downloaded) =>
        downloaded.Any(model => model.Equals(Name, StringComparison.OrdinalIgnoreCase))
            ? downloaded
            : [Name, .. downloaded];

    public static string Tip(double vramGb, double ramGb)
    {
        var size = DownloadGb.ToString("0.#", CultureInfo.InvariantCulture);
        return Fit(vramGb, ramGb) switch
        {
            Runability.Gpu =>
                $"Recommended: {Title}, made for this mod. {size} GB, fits your " +
                $"{vramGb.ToString("0.#", CultureInfo.InvariantCulture)} GB GPU.",
            Runability.CpuOnly =>
                $"Recommended: {Title}, made for this mod. {size} GB. Your GPU cannot hold it, " +
                "so it runs on the CPU and replies are slower.",
            _ => OllamaDriver.RecommendationTip(vramGb)
        };
    }

    public static CatalogModel CatalogEntry() => new(
        Repository,
        Description,
        ["recommended"],
        [new CatalogTag(Quantization, ParametersB)]);

    public static IReadOnlyList<CatalogModel> Feature(IReadOnlyList<CatalogModel> models) =>
    [
        CatalogEntry(),
        .. models.Where(model => !model.Name.Equals(Repository, StringComparison.OrdinalIgnoreCase))
    ];

    public static string DisplayName(CatalogModel model) =>
        model.Name.Equals(Repository, StringComparison.OrdinalIgnoreCase) ? Title : model.Name;
}
