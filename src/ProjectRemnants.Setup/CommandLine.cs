using ProjectRemnants.Setup.Install;

namespace ProjectRemnants.Setup;

internal static class CommandLine
{
    public static int Run(string[] args)
    {
        try
        {
            if (args is ["--install", var configPath, var agentPath])
            {
                ProjectZomboidConfig.Install(configPath, agentPath);
                return 0;
            }

            if (args is ["--uninstall", var uninstallConfigPath])
            {
                ProjectZomboidConfig.Uninstall(uninstallConfigPath);
                return 0;
            }

            return 64;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Project Remnants Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
