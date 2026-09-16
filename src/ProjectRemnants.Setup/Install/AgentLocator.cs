namespace ProjectRemnants.Setup.Install;

public static class AgentLocator
{
    public const string AgentFileName = "NPCFW.jar";

    public static string? Find(string? applicationDirectory = null)
    {
        try
        {
            DirectoryInfo? directory = new(Path.GetFullPath(
                applicationDirectory ?? AppContext.BaseDirectory));
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

                directory = directory.Parent;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }
}

