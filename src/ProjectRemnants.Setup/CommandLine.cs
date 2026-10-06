using ProjectRemnants.Setup.Install;
using ProjectRemnants.Setup.Llm;
using ProjectRemnants.Setup.Voice;

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
                Console.WriteLine("Project Remnants agent installed.");
                return 0;
            }

            if (args is ["--uninstall", var uninstallConfigPath])
            {
                ProjectZomboidConfig.Uninstall(uninstallConfigPath);
                Console.WriteLine("Project Remnants agent uninstalled.");
                return 0;
            }

            if (args is ["--detect"])
            {
                var installs = GameLocator.FindAll();
                if (installs.Count == 0)
                {
                    Console.WriteLine("No Project Zomboid installation was detected.");
                    return 0;
                }

                foreach (var install in installs)
                {
                    Console.WriteLine($"{install.ConfigPath} ({install.Source})");
                    var agent = AgentLocator.Find(gameDirectories: [install.Directory]);
                    if (agent is null)
                    {
                        Console.WriteLine(
                            "  NPCFW.jar was not found. Subscribe to the mod on Steam or pass its path to --install.");
                        continue;
                    }

                    Console.WriteLine($"  NPCFW.jar: {agent}");
                    Console.WriteLine(
                        $"  Run: ProjectRemnantsSetup --install {Quote(install.ConfigPath)} {Quote(agent)}");
                }

                return 0;
            }

            if (args is ["--ollama-models"])
            {
                var ollama = new OllamaDriver();
                try
                {
                    foreach (var model in ollama.GetDownloadedModels())
                    {
                        Console.WriteLine(model);
                    }
                }
                finally
                {
                    ollama.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return 0;
            }

            if (args is ["--voice-install", var modelId])
            {
                var voiceModel = VoiceCatalog.Models.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"Unknown voice model '{modelId}'. Choices: {string.Join(", ", VoiceCatalog.Models.Select(m => m.Id))}.");
                var voiceInstaller = new VoiceInstaller();
                var console = new Progress<VoiceProgress>(progress =>
                    Console.WriteLine(progress.Percent is int percent
                        ? $"{progress.Message} — {percent}%"
                        : progress.Message));
                voiceInstaller.InstallAsync(voiceModel, console).GetAwaiter().GetResult();
                Console.WriteLine("Voice input is ready.");
                return 0;
            }

            if (args is ["--voice-status"])
            {
                var status = new VoiceInstaller().GetStatus();
                Console.WriteLine($"Root: {status.Root}");
                Console.WriteLine($"Engine installed: {status.EngineInstalled}");
                Console.WriteLine($"Active model: {status.ActiveModelFile ?? "(none)"}");
                foreach (var file in status.InstalledModelFiles)
                {
                    Console.WriteLine($"Model file: {file}");
                }

                return 0;
            }

            if (args is ["--voice-remove"])
            {
                new VoiceInstaller().Remove();
                Console.WriteLine("Voice input was removed.");
                return 0;
            }

            if (args is ["--llm-enable-ollama", var llmUserDir, var ollamaModel])
            {
                LlmConfigurationStore.SaveOllama(llmUserDir, ollamaModel);
                Console.WriteLine($"NPCFW will use the local Ollama model '{ollamaModel}'.");
                return 0;
            }

            if (args is ["--llm-enable-remote", var remoteUserDir, var endpoint, var remoteModel, var apiKey])
            {
                var profile = new RemoteApiProfile(endpoint, remoteModel, apiKey);
                var bridge = new RemoteApiBridge();
                try
                {
                    bridge.StartAsync(profile.Endpoint, profile.ApiKey).GetAwaiter().GetResult();
                    LlmConfigurationStore.SaveRemoteProfile(remoteUserDir, profile);
                    LlmConfigurationStore.SaveOpenAiBridge(remoteUserDir, profile.Model, bridge.Endpoint, bridge.LocalToken);
                    Console.WriteLine($"Secure API bridge exposed at {bridge.Endpoint} while this process runs.");
                    Console.WriteLine("Keep this process alive to bridge requests.");
                    Console.ReadLine();
                }
                finally
                {
                    bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                return 0;
            }

            if (args is ["--llamacpp-install"])
            {
                var llama = new LlamaCppDriver();
                try
                {
                    var console = new Progress<OllamaProgress>(progress =>
                        Console.WriteLine(progress.Percent is int percent
                            ? $"{progress.Message} — {percent}%"
                            : progress.Message));
                    var executable = llama.InstallAsync(console).GetAwaiter().GetResult();
                    Console.WriteLine($"llama-server installed: {executable}");
                }
                finally
                {
                    llama.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return 0;
            }

            if (args is ["--llm-enable-llamacpp", var llamaUserDir, var ggufPath])
            {
                if (!File.Exists(ggufPath))
                {
                    throw new FileNotFoundException("Could not find the GGUF model file.", ggufPath);
                }

                var llamaDriver = new LlamaCppDriver();
                try
                {
                    if (llamaDriver.FindExecutable() is null)
                    {
                        Console.WriteLine("llama-server not found. Downloading llama.cpp...");
                        var install = new Progress<OllamaProgress>(progress =>
                            Console.WriteLine(progress.Message));
                        llamaDriver.InstallAsync(install).GetAwaiter().GetResult();
                    }

                    Console.WriteLine($"Loading {Path.GetFileName(ggufPath)} with llama-server...");
                    llamaDriver.EnsureServerAsync(ggufPath).GetAwaiter().GetResult();
                    var servedModel = llamaDriver.GetServedModelNameAsync().GetAwaiter().GetResult()
                        ?? Path.GetFileNameWithoutExtension(ggufPath);
                    var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                    LlmConfigurationStore.SaveOpenAiBridge(llamaUserDir, servedModel, llamaDriver.Endpoint, token);
                    Console.WriteLine($"llama.cpp is serving '{servedModel}' at {llamaDriver.Endpoint}");
                    Console.WriteLine("Press Enter to stop llama-server.");
                    Console.ReadLine();
                }
                finally
                {
                    llamaDriver.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return 0;
            }

            if (args is ["--llm-disable", var disableUserDir])
            {
                LlmConfigurationStore.Disable(disableUserDir);
                Console.WriteLine("LLM integration disabled.");
                return 0;
            }

            if (args is ["--help"] or ["-h"] or ["--?"] or { Length: 0 })
            {
                PrintUsage();
                return 0;
            }

            PrintUsage();
            return 64;
        }
        catch (Exception exception)
        {
#if WINDOWS
            MessageBox.Show(
                exception.Message,
                "Project Remnants Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
#else
            Console.Error.WriteLine($"Error: {exception.Message}");
#endif
            return 1;
        }
    }

    private static string Quote(string path) =>
        path.Contains('"') ? $"\"{path.Replace("\"", "\\\"")}\"" : $"\"{path}\"";

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Project Remnants Setup

            Game setup:
              --detect                                  List detected Project Zomboid installs
              --install <ProjectZomboid64.json> <NPCFW.jar>   Install/repair the Java agent
              --uninstall <ProjectZomboid64.json>       Remove the Java agent

            Local LLM (Ollama):
              --ollama-models                           List downloaded Ollama models
              --llm-enable-ollama <userDir> <model>     Use a local Ollama model
              --llm-disable <userDir>                   Disable the LLM integration

            Local LLM (llama.cpp):
              --llamacpp-install                        Download llama-server
              --llm-enable-llamacpp <userDir> <model.gguf>  Run a GGUF model via llama-server

            Remote LLM endpoint:
              --llm-enable-remote <userDir> <endpoint> <model> <apiKey>

            Voice input:
              --voice-install <modelId> (fast | accurate)
              --voice-status
              --voice-remove

            Options:
              --help                                    Show this text

            On Windows, run without arguments to open the GUI.
            """);
    }
}
