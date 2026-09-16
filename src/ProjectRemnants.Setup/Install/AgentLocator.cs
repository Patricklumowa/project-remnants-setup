namespace ProjectRemnants.Setup.Install;

public static class AgentLocator
{
    public const string AgentFileName = "NPCFW.jar";

    public static string? Find(
        string? applicationDirectory = null,
        IEnumerable<string>? gameDirectories = null)
    {
        var nearby = FindNearby(applicationDirectory ?? AppContext.BaseDirectory);
        if (nearby is not null)
        {
            return nearby;
        }

        foreach (var gameDirectory in gameDirectories ?? [])
        {
            var workshop = FindWorkshopCopy(gameDirectory);
            if (workshop is not null)
            {
                return workshop;
            }
        }

        return null;
    }

    private static string? FindNearby(string applicationDirectory)
    {
        try
        {
            DirectoryInfo? directory = new(Path.GetFullPath(applicationDirectory));
            for (var depth = 0; directory is not null && depth < 8; depth++)
            {
                var beside = Path.Combine(directory.FullName, AgentFileName);
                if (File.Exists(beside))
                {
                    return beside;
                }

                var modRoot = Path.Combine(
                    directory.FullName, "mods", "ProjectRemnants", AgentFileName);
                if (File.Exists(modRoot))
                {
                    return modRoot;
                }

                var root = Path.Combine(
                    directory.FullName, "mods", "ProjectRemnants", "root", AgentFileName);
                if (File.Exists(root))
                {
                    return root;
                }

                directory = directory.Parent;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static string? FindWorkshopCopy(string gameDirectory)
    {
        try
        {
            var common = Directory.GetParent(Path.GetFullPath(gameDirectory));
            var steamApps = common?.Parent;
            var workshop = steamApps is null
                ? null
                : Path.Combine(steamApps.FullName, "workshop", "content", "108600");
            if (workshop is null || !Directory.Exists(workshop))
            {
                return null;
            }

            foreach (var item in Directory.EnumerateDirectories(workshop)
                .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var mod = Path.Combine(item, "mods", "ProjectRemnants");
                var candidates = new[]
                {
                    Path.Combine(mod, AgentFileName),
                    Path.Combine(mod, "root", AgentFileName)
                };
                var found = candidates.FirstOrDefault(File.Exists);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }
}
