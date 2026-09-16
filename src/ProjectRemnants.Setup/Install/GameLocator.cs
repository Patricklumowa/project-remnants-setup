using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProjectRemnants.Setup.Install;

public sealed record GameInstall(string Directory, string ConfigPath, string Source);

public static class GameLocator
{
    private const string AppId = "108600";
    private const string ConfigName = "ProjectZomboid64.json";
    private static readonly Regex InstallDirectoryPattern = new(
        "\\\"installdir\\\"\\s+\\\"(?<value>[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LibraryPathPattern = new(
        "\\\"path\\\"\\s+\\\"(?<value>[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<GameInstall> FindAll(string? applicationDirectory = null)
    {
        var found = new List<GameInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TryRegistryInstall(found, seen);
        TryContainingSteamLibrary(applicationDirectory ?? AppContext.BaseDirectory, found, seen);
        TryRegisteredSteamLibraries(found, seen);
        return found;
    }

    private static void TryRegistryInstall(List<GameInstall> found, HashSet<string> seen)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 108600");
            AddCandidate(key?.GetValue("InstallLocation") as string, "Windows registry", found, seen);
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
        }
    }

    private static void TryContainingSteamLibrary(
        string startDirectory, List<GameInstall> found, HashSet<string> seen)
    {
        try
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));
            while (directory is not null)
            {
                if (directory.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    TrySteamApps(directory.FullName, "Workshop library", found, seen);
                    return;
                }

                directory = directory.Parent;
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryRegisteredSteamLibraries(List<GameInstall> found, HashSet<string> seen)
    {
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
        }

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return;
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(steamPath)
        };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(libraryFile))
            {
                var text = File.ReadAllText(libraryFile);
                foreach (Match match in LibraryPathPattern.Matches(text))
                {
                    var path = match.Groups["value"].Value.Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        libraries.Add(Path.GetFullPath(path));
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        foreach (var library in libraries)
        {
            TrySteamApps(Path.Combine(library, "steamapps"), "Steam library", found, seen);
        }
    }

    private static void TrySteamApps(
        string steamApps, string source, List<GameInstall> found, HashSet<string> seen)
    {
        var manifest = Path.Combine(steamApps, $"appmanifest_{AppId}.acf");
        if (!File.Exists(manifest))
        {
            return;
        }

        try
        {
            var match = InstallDirectoryPattern.Match(File.ReadAllText(manifest));
            if (!match.Success)
            {
                return;
            }

            AddCandidate(
                Path.Combine(steamApps, "common", match.Groups["value"].Value),
                source,
                found,
                seen);
        }
        catch (Exception)
        {
        }
    }

    private static void AddCandidate(
        string? directory, string source, List<GameInstall> found, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var fullDirectory = Path.GetFullPath(directory.Trim().Trim('"'));
            var configPath = Path.Combine(fullDirectory, ConfigName);
            if (File.Exists(configPath) && seen.Add(configPath))
            {
                found.Add(new GameInstall(fullDirectory, configPath, source));
            }
        }
        catch (Exception)
        {
        }
    }
}

