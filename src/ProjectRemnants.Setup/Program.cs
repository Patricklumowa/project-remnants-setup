using ProjectRemnants.Setup.Install;

namespace ProjectRemnants.Setup;

internal static class Program
{
#if WINDOWS
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
#else
    private static int Main(string[] args) => CommandLine.Run(args);
#endif
}

