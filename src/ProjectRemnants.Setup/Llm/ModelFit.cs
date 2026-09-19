using System.Globalization;

namespace ProjectRemnants.Setup.Llm;

public enum Runability
{
    Unknown,
    Gpu,
    CpuOnly,
    TooBig
}

public static class ModelFit
{
    public const double GigabytesPerBillionParameters = 0.6;

    private const double GpuBudgetFactor = 0.88;
    private const double RamBudgetFactor = 0.70;

    public static double EstimateDownloadGb(double parametersB) =>
        parametersB <= 0 ? 0 : parametersB * GigabytesPerBillionParameters;

    public static Runability Evaluate(double downloadGb, double vramGb, double ramGb)
    {
        if (downloadGb <= 0)
        {
            return Runability.Unknown;
        }

        if (vramGb > 0 && downloadGb <= vramGb * GpuBudgetFactor)
        {
            return Runability.Gpu;
        }

        if (ramGb > 0 && downloadGb <= ramGb * RamBudgetFactor)
        {
            return Runability.CpuOnly;
        }

        return Runability.TooBig;
    }

    public static string Describe(Runability runability) => runability switch
    {
        Runability.Gpu => "Runnable on your GPU",
        Runability.CpuOnly => "Runnable, CPU only",
        Runability.TooBig => "Yeah ur gpu aint running ts twin",
        _ => "Size unknown"
    };

    public static bool TryParseParametersB(string tag, out double billions)
    {
        billions = 0;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim().ToLowerInvariant();
        var parts = text.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var product = 1.0;
        foreach (var part in parts)
        {
            if (!TryParseScale(part, out var value))
            {
                return false;
            }

            product *= value;
        }

        if (product <= 0)
        {
            return false;
        }

        billions = Math.Round(product, 3);
        return true;
    }

    private static bool TryParseScale(string token, out double billions)
    {
        billions = 0;
        var suffix = token[^1];
        var number = suffix is 'b' or 'm' ? token[..^1] : token;
        if (!double.TryParse(
                number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            return false;
        }

        billions = suffix switch
        {
            'b' => value,
            'm' => value / 1000,
            _ => char.IsDigit(suffix) ? value : 0
        };
        return billions > 0;
    }
}
