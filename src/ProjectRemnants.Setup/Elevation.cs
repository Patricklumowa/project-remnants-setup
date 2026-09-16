using System.ComponentModel;
using System.Diagnostics;

namespace ProjectRemnants.Setup;

internal static class Elevation
{
    public static bool Run(params string[] arguments)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not locate the setup executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The elevated setup process did not start.");
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return false;
        }
    }
}

