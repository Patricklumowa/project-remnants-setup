using ProjectRemnants.Setup.Install;

namespace ProjectRemnants.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length != 0)
        {
            return CommandLine.Run(args);
        }

        Application.Run(new MainForm());
        return 0;
    }
}

