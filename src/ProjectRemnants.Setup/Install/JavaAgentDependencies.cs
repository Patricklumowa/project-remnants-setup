using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectRemnants.Setup.Install;

public static class JavaAgentDependencies
{
    private const string ManifestName = ".project-remnants-files.json";
    private const string BackupDirectoryName = ".project-remnants-backups";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] RequiredBinFiles =
    [
        "instrument.dll",
        "java.dll",
        "jli.dll",
        "vcruntime140.dll",
        "api-ms-win-crt-convert-l1-1-0.dll",
        "api-ms-win-crt-heap-l1-1-0.dll",
        "api-ms-win-crt-runtime-l1-1-0.dll",
        "api-ms-win-crt-stdio-l1-1-0.dll",
        "api-ms-win-crt-string-l1-1-0.dll"
    ];
    private static readonly string[] OptionalBinFiles =
    [
        "msvcp140.dll",
        "ucrtbase.dll",
        "vcruntime140_1.dll"
    ];

    public static int Install(string configPath)
    {
        var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new InvalidOperationException("Could not locate the Project Zomboid directory.");
        var manifest = LoadManifest(gameDirectory);
        var copied = 0;
        var legacyInstall = HasLegacyInstall(gameDirectory) || HasCopiedCoreDependencies(gameDirectory);

        foreach (var dependency in FindDependencies(gameDirectory, requireCoreFiles: true))
        {
            var target = Path.Combine(gameDirectory, dependency.Name);
            var entry = manifest.Files.FirstOrDefault(file =>
                file.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                var legacyBackups = FindLegacyBackups(target);
                if (legacyInstall)
                {
                    entry = legacyBackups.Length == 0
                        ? new ManifestEntry(dependency.Name, true, null)
                        : BackupOriginal(gameDirectory, dependency.Name, legacyBackups[0]);
                }
                else if (File.Exists(target) && FilesMatch(dependency.Source, target))
                {
                    continue;
                }
                else if (File.Exists(target))
                {
                    entry = BackupOriginal(gameDirectory, dependency.Name, target);
                }
                else
                {
                    entry = new ManifestEntry(dependency.Name, true, null);
                }

                manifest.Files.Add(entry);
                SaveManifest(gameDirectory, manifest);
            }

            File.Copy(dependency.Source, target, overwrite: true);
            copied++;
        }

        return copied;
    }

    public static void Uninstall(string configPath, bool legacyInstall = false)
    {
        var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new InvalidOperationException("Could not locate the Project Zomboid directory.");
        var manifestPath = Path.Combine(gameDirectory, ManifestName);
        legacyInstall |= HasLegacyInstall(gameDirectory) || HasCopiedCoreDependencies(gameDirectory);
        if (File.Exists(manifestPath))
        {
            UninstallManifest(gameDirectory);
            DeleteLegacyDllBackups(gameDirectory);
        }
        else if (legacyInstall)
        {
            UninstallLegacyDependencies(gameDirectory);
        }

        DeleteRootArtifacts(gameDirectory);
    }

    private static void UninstallManifest(string gameDirectory)
    {
        var manifestPath = Path.Combine(gameDirectory, ManifestName);
        var manifest = LoadManifest(gameDirectory);
        var backupDirectory = Path.Combine(gameDirectory, BackupDirectoryName);
        foreach (var entry in manifest.Files.ToArray())
        {
            var target = Path.Combine(gameDirectory, entry.Name);
            if (entry.Created)
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            else
            {
                var backup = Path.Combine(backupDirectory, entry.Backup!);
                if (!File.Exists(backup))
                {
                    throw new FileNotFoundException($"Could not restore the original {entry.Name}.", backup);
                }

                RestoreFile(backup, target);
            }

            manifest.Files.Remove(entry);
            SaveManifest(gameDirectory, manifest);
        }

        File.Delete(manifestPath);
        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, recursive: true);
        }
    }

    private static void UninstallLegacyDependencies(string gameDirectory)
    {
        foreach (var dependency in FindDependencies(gameDirectory, requireCoreFiles: false))
        {
            var target = Path.Combine(gameDirectory, dependency.Name);
            var backups = FindLegacyBackups(target);
            if (backups.Length != 0)
            {
                RestoreFile(backups[0], target);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }

            foreach (var backup in backups)
            {
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
        }
    }

    private static void DeleteLegacyDllBackups(string gameDirectory)
    {
        foreach (var dependency in FindDependencies(gameDirectory, requireCoreFiles: false))
        {
            var target = Path.Combine(gameDirectory, dependency.Name);
            foreach (var backup in FindLegacyBackups(target))
            {
                File.Delete(backup);
            }
        }
    }

    private static void DeleteRootArtifacts(string gameDirectory)
    {
        foreach (var name in new[]
        {
            "NPCFW.jar",
            "NPCFWNative.dll",
            "NPCFW_combat_log.txt",
            "NPCFW_vehicle_entry_debug.log"
        })
        {
            var path = Path.Combine(gameDirectory, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (var pattern in new[] { "NPCFW_animdebug_*.csv", "NPCFW_character_*.log" })
        {
            foreach (var path in Directory.EnumerateFiles(
                gameDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
            }
        }

        foreach (var pattern in new[]
        {
            ".project-remnants-files.json.*.tmp",
            "ProjectZomboid64.json.*.tmp"
        })
        {
            foreach (var path in Directory.EnumerateFiles(
                gameDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
            }
        }

        var driveRecords = Path.Combine(gameDirectory, "NPCFW_drive_records");
        if (Directory.Exists(driveRecords))
        {
            Directory.Delete(driveRecords, recursive: true);
        }
    }

    private static IReadOnlyList<Dependency> FindDependencies(
        string gameDirectory,
        bool requireCoreFiles)
    {
        var bin = Path.Combine(gameDirectory, "jre64", "bin");
        var dependencies = new Dictionary<string, Dependency>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RequiredBinFiles)
        {
            Add(dependencies, Path.Combine(bin, name), name, required: requireCoreFiles);
        }

        Add(dependencies, Path.Combine(bin, "server", "jvm.dll"), "jvm.dll", required: requireCoreFiles);
        foreach (var name in OptionalBinFiles)
        {
            Add(dependencies, Path.Combine(bin, name), name, required: false);
        }

        if (Directory.Exists(bin))
        {
            foreach (var path in Directory.EnumerateFiles(
                bin, "api-ms-win-crt-*.dll", SearchOption.TopDirectoryOnly))
            {
                Add(dependencies, path, Path.GetFileName(path), required: false);
            }
        }

        return dependencies.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasLegacyInstall(string gameDirectory) => Directory.EnumerateFiles(
        gameDirectory,
        "ProjectZomboid64.json.ProjectRemnantsBackup.*",
        SearchOption.TopDirectoryOnly).Any();

    private static bool HasCopiedCoreDependencies(string gameDirectory)
    {
        var bin = Path.Combine(gameDirectory, "jre64", "bin");
        return new[]
        {
            (Path.Combine(bin, "instrument.dll"), Path.Combine(gameDirectory, "instrument.dll")),
            (Path.Combine(bin, "java.dll"), Path.Combine(gameDirectory, "java.dll")),
            (Path.Combine(bin, "jli.dll"), Path.Combine(gameDirectory, "jli.dll")),
            (Path.Combine(bin, "server", "jvm.dll"), Path.Combine(gameDirectory, "jvm.dll"))
        }.All(pair => File.Exists(pair.Item1) && File.Exists(pair.Item2) && FilesMatch(pair.Item1, pair.Item2));
    }

    private static string[] FindLegacyBackups(string target) => Directory.EnumerateFiles(
        Path.GetDirectoryName(target)!,
        $"{Path.GetFileName(target)}.ProjectRemnantsBackup.*",
        SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    private static ManifestEntry BackupOriginal(string gameDirectory, string name, string source)
    {
        var backupDirectory = Path.Combine(gameDirectory, BackupDirectoryName);
        Directory.CreateDirectory(backupDirectory);
        var backupName = $"{name}.{Guid.NewGuid():N}.original";
        File.Copy(source, Path.Combine(backupDirectory, backupName));
        return new ManifestEntry(name, false, backupName);
    }

    private static void Add(
        IDictionary<string, Dependency> dependencies,
        string source,
        string name,
        bool required)
    {
        if (!File.Exists(source))
        {
            if (required)
            {
                throw new FileNotFoundException($"Missing required Java runtime DLL: {name}", source);
            }

            return;
        }

        dependencies.TryAdd(name, new Dependency(source, name));
    }

    private static Manifest LoadManifest(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, ManifestName);
        if (!File.Exists(path))
        {
            return new Manifest();
        }

        if (new FileInfo(path).Length > 65_536)
        {
            throw new InvalidDataException("The Project Remnants install manifest is too large.");
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path, Encoding.UTF8))
            ?? throw new InvalidDataException("The Project Remnants install manifest is invalid.");
        if (manifest.Files.Any(entry =>
            entry.Name != Path.GetFileName(entry.Name) ||
            !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            entry.Created == (entry.Backup is not null) ||
            entry.Backup is not null && entry.Backup != Path.GetFileName(entry.Backup)) ||
            manifest.Files.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            manifest.Files.Count)
        {
            throw new InvalidDataException("The Project Remnants install manifest is invalid.");
        }

        return manifest;
    }

    private static void SaveManifest(string gameDirectory, Manifest manifest)
    {
        var path = Path.Combine(gameDirectory, ManifestName);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
                Utf8WithoutBom);
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

    private static bool FilesMatch(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length)
        {
            return false;
        }

        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).SequenceEqual(SHA256.HashData(secondStream));
    }

    private static void RestoreFile(string backup, string target)
    {
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(backup, temporary);
            if (File.Exists(target))
            {
                File.Replace(temporary, target, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, target);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record Dependency(string Source, string Name);
    private sealed record ManifestEntry(string Name, bool Created, string? Backup);

    private sealed class Manifest
    {
        public List<ManifestEntry> Files { get; init; } = [];
    }
}
