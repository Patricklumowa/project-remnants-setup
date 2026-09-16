using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectRemnants.Setup.Llm;

public sealed record RemoteApiProfile(string Endpoint, string Model, string ApiKey);

public static class LlmConfigurationStore
{
    private const string ProviderFile = "NPCFW_LLM_PROVIDER.json";
    private const string LegacyModelFile = "NPCFW_LLM_MODEL.txt";
    private const string ProfileFile = "NPCFW_LLM_API_PROFILE.json";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultUserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid");

    public static void SaveOllama(string userDirectory, string model)
    {
        model = RequireText(model, "model");
        WriteProvider(userDirectory, new
        {
            provider = "ollama",
            model
        });
        WriteText(Path.Combine(RequireDirectory(userDirectory), LegacyModelFile), model);
    }

    public static void SaveOpenAiBridge(
        string userDirectory, string model, string endpoint, string bridgeToken)
    {
        WriteProvider(userDirectory, new
        {
            provider = "openai",
            model = RequireText(model, "model"),
            endpoint = RequireHttpEndpoint(endpoint),
            apiKey = RequireText(bridgeToken, "bridge token")
        });
    }

    public static void Disable(string userDirectory)
    {
        var directory = Path.GetFullPath(userDirectory);
        DeleteIfPresent(Path.Combine(directory, ProviderFile));
        DeleteIfPresent(Path.Combine(directory, LegacyModelFile));
    }

    public static void SaveRemoteProfile(string userDirectory, RemoteApiProfile profile)
    {
        var value = new
        {
            endpoint = RequireHttpEndpoint(profile.Endpoint),
            model = RequireText(profile.Model, "model"),
            apiKey = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(
                RequireText(profile.ApiKey, "API key"))))
        };
        WriteJson(Path.Combine(RequireDirectory(userDirectory), ProfileFile), value);
    }

    public static RemoteApiProfile? LoadRemoteProfile(string userDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(userDirectory), ProfileFile);
        if (!File.Exists(path) || new FileInfo(path).Length > 32_768)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            var endpoint = root.GetProperty("endpoint").GetString() ?? string.Empty;
            var model = root.GetProperty("model").GetString() ?? string.Empty;
            var protectedKey = Convert.FromBase64String(
                root.GetProperty("apiKey").GetString() ?? string.Empty);
            var keyBytes = Unprotect(protectedKey);
            try
            {
                return new RemoteApiProfile(
                    RequireHttpEndpoint(endpoint),
                    RequireText(model, "model"),
                    RequireText(Encoding.UTF8.GetString(keyBytes), "API key"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteProvider(string userDirectory, object value) =>
        WriteJson(Path.Combine(RequireDirectory(userDirectory), ProviderFile), value);

    private static void WriteJson(string path, object value) =>
        WriteText(path, JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteText(string path, string value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, value + Environment.NewLine, Utf8WithoutBom);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string RequireDirectory(string path)
    {
        var directory = Path.GetFullPath(RequireText(path, "Zomboid user directory"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string RequireHttpEndpoint(string value)
    {
        value = RequireText(value, "endpoint").TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new ArgumentException("Use an HTTPS endpoint or loopback HTTP endpoint.");
        }

        return value;
    }

    private static string RequireText(string value, string name)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new ArgumentException($"Enter a {name}.");
        }

        return value;
    }

    private static byte[] Protect(byte[] value) =>
        ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] value) =>
        ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser);

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

