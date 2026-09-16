using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectRemnants.Setup.Install;

public sealed record ConfigStatus(bool Installed, string? AgentPath, int ObsoleteEntries);

public static class ProjectZomboidConfig
{
    private const string BackupSuffix = ".npcfw-backup";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static ConfigStatus Inspect(string configPath)
    {
        var root = ReadRoot(configPath);
        var arguments = GetVmArguments(root, create: false);
        string? installedPath = null;
        var obsolete = 0;

        if (arguments is not null)
        {
            foreach (var node in arguments)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var argument))
                {
                    continue;
                }

                if (TryGetNpcfwAgentPath(argument, out var path))
                {
                    installedPath ??= path;
                }
                else if (IsObsoleteNpcfwArgument(argument))
                {
                    obsolete++;
                }
            }
        }

        return new ConfigStatus(installedPath is not null, installedPath, obsolete);
    }

    public static void Install(string configPath, string agentPath)
    {
        EnsureGameStopped();
        configPath = RequireFile(configPath, "ProjectZomboid64.json");
        agentPath = RequireFile(agentPath, AgentLocator.AgentFileName);
        var root = ReadRoot(configPath);
        var arguments = GetVmArguments(root, create: true)!;
        RemoveNpcfwArguments(arguments);
        arguments.Insert(0, $"-javaagent:{NormalizeAgentPath(agentPath)}");
        JavaAgentDependencies.Install(configPath);
        Save(configPath, root, createBackup: true);
    }

    public static void Uninstall(string configPath)
    {
        EnsureGameStopped();
        configPath = RequireFile(configPath, "ProjectZomboid64.json");
        var backup = configPath + BackupSuffix;
        var legacyBackups = Directory.EnumerateFiles(
            Path.GetDirectoryName(configPath)!,
            "ProjectZomboid64.json.ProjectRemnantsBackup.*",
            SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (File.Exists(backup))
        {
            RestoreBackup(configPath, backup);
        }
        else if (legacyBackups.Length != 0)
        {
            RestoreBackup(configPath, legacyBackups[0]);
        }
        RemoveInjectedArguments(configPath);
        JavaAgentDependencies.Uninstall(configPath, legacyBackups.Length != 0);
        foreach (var legacyBackup in legacyBackups)
        {
            if (File.Exists(legacyBackup))
            {
                File.Delete(legacyBackup);
            }
        }
    }

    private static void RemoveInjectedArguments(string configPath)
    {
        var root = ReadRoot(configPath);
        var arguments = GetVmArguments(root, create: false);
        if (arguments is not null && RemoveNpcfwArguments(arguments) != 0)
        {
            Save(configPath, root, createBackup: false);
        }
    }

    private static void EnsureGameStopped()
    {
        var running = new List<Process>();
        try
        {
            foreach (var name in new[] { "ProjectZomboid64", "ProjectZomboid32", "ProjectZomboid" })
            {
                running.AddRange(Process.GetProcessesByName(name));
            }

            if (running.Count != 0)
            {
                var details = string.Join(", ", running.Select(process =>
                    $"{process.ProcessName} (PID {process.Id})"));
                throw new InvalidOperationException(
                    $"Project Zomboid is still running: {details}. Close it completely and try again.");
            }
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    private static void RestoreBackup(string configPath, string backup)
    {
        _ = ReadRoot(backup);
        var temporary = configPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(backup, temporary);
            File.Replace(temporary, configPath, null, ignoreMetadataErrors: true);
            File.Delete(backup);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static JsonObject ReadRoot(string configPath)
    {
        var text = File.ReadAllText(configPath, Encoding.UTF8);
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("ProjectZomboid64.json is not a JSON object.");
    }

    private static JsonArray? GetVmArguments(JsonObject root, bool create)
    {
        if (root["vmArgs"] is JsonArray existing)
        {
            return existing;
        }

        if (!create)
        {
            return null;
        }

        var created = new JsonArray();
        root["vmArgs"] = created;
        return created;
    }

    private static int RemoveNpcfwArguments(JsonArray arguments)
    {
        var removed = 0;
        for (var index = arguments.Count - 1; index >= 0; index--)
        {
            if (arguments[index] is JsonValue value &&
                value.TryGetValue<string>(out var argument) &&
                (TryGetNpcfwAgentPath(argument, out _) || IsObsoleteNpcfwArgument(argument)))
            {
                arguments.RemoveAt(index);
                removed++;
            }
        }

        return removed;
    }

    private static bool TryGetNpcfwAgentPath(string argument, out string path)
    {
        const string prefix = "-javaagent:";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            argument.Contains(AgentLocator.AgentFileName, StringComparison.OrdinalIgnoreCase))
        {
            path = argument[prefix.Length..];
            var optionSeparator = path.IndexOf('=');
            if (optionSeparator >= 0)
            {
                path = path[..optionSeparator];
            }

            return true;
        }

        path = string.Empty;
        return false;
    }

    private static bool IsObsoleteNpcfwArgument(string argument) =>
        argument.Equals("-agentlib:zbNative", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("-agentpath:", StringComparison.OrdinalIgnoreCase) &&
        argument.Contains("NPCFWNative.dll", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAgentPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string RequireFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"Select {description} first.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Could not find {description}.", fullPath);
        }

        return fullPath;
    }

    private static void Save(string configPath, JsonObject root, bool createBackup)
    {
        if (createBackup)
        {
            var backup = configPath + BackupSuffix;
            if (!File.Exists(backup))
            {
                File.Copy(configPath, backup);
            }
        }

        var temporary = configPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var json = root.ToJsonString(JsonOptions) + Environment.NewLine;
            File.WriteAllText(temporary, json, Utf8WithoutBom);
            _ = JsonNode.Parse(File.ReadAllText(temporary, Encoding.UTF8))
                ?? throw new InvalidDataException("Generated JSON was empty.");
            File.Replace(temporary, configPath, null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
