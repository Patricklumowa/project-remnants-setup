using System.Net.Http.Headers;
using System.Text.Json;

namespace ProjectRemnants.Setup.Llm;

public static class RemoteModelDiscovery
{
    public static async Task<IReadOnlyList<string>> ListAsync(
        string endpoint, string apiKey, CancellationToken cancellationToken = default)
    {
        var uri = BuildModelsUri(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribeFailure(response.StatusCode, body));
        }

        return Parse(body);
    }

    public static Uri BuildModelsUri(string endpoint)
    {
        var value = endpoint?.Trim().TrimEnd('/') ?? string.Empty;
        if (value.Length == 0)
        {
            throw new ArgumentException("Enter an endpoint.");
        }

        const string chatCompletions = "/chat/completions";
        if (value.EndsWith(chatCompletions, StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^chatCompletions.Length];
        }

        const string models = "/models";
        if (!value.EndsWith(models, StringComparison.OrdinalIgnoreCase))
        {
            value += models;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new ArgumentException("Use an HTTPS endpoint or loopback HTTP endpoint.");
        }

        return uri;
    }

    public static IReadOnlyList<string> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetArray(root, out var array))
            {
                return [];
            }

            var ids = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                var id = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object &&
                      item.TryGetProperty("id", out var idElement) &&
                      idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString()
                        : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id.Trim());
                }
            }

            return ids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            array = data;
            return true;
        }

        array = default;
        return false;
    }

    private static string DescribeFailure(System.Net.HttpStatusCode status, string body)
    {
        var message = ExtractMessage(body);
        var detail = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : $" {message}";
        return status switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                $"The API key was rejected (401). Check the key.{detail}",
            System.Net.HttpStatusCode.NotFound =>
                $"The endpoint has no /models route (404). Check the base URL.{detail}",
            System.Net.HttpStatusCode.TooManyRequests =>
                $"The endpoint is rate limiting (429). Try again shortly.{detail}",
            _ => $"The endpoint returned {(int)status} {status}.{detail}"
        };
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }

                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }

        var trimmed = body.Trim();
        return trimmed.Length > 200 ? trimmed[..200] + "..." : trimmed;
    }
}
