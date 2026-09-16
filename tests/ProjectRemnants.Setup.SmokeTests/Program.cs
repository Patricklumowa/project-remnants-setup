using System.Text;
using System.Text.Json;
using ProjectRemnants.Setup.Install;
using ProjectRemnants.Setup.Llm;

var root = Path.Combine(Path.GetTempPath(), $"ProjectRemnantsSetup-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    TestConfigInstall(root);
    TestSteamDiscovery(root);
    TestLlmConfiguration(root);
    await TestBridgeLifecycle();
    Console.WriteLine("All smoke tests passed.");
    return 0;
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestConfigInstall(string root)
{
    var game = Path.Combine(root, "game");
    Directory.CreateDirectory(game);
    var config = Path.Combine(game, "ProjectZomboid64.json");
    var agent = Path.Combine(root, "NPCFW.jar");
    File.WriteAllText(agent, "test");
    File.WriteAllText(config, """
        {
          "mainClass": "zombie/gameStates/MainScreenState",
          "vmArgs": [
            "-agentlib:zbNative",
            "-Xmx4096m"
          ]
        }
        """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    ProjectZomboidConfig.Install(config, agent);
    var bytes = File.ReadAllBytes(config);
    Assert(bytes.Length >= 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
        "The config contains a UTF-8 BOM.");
    var status = ProjectZomboidConfig.Inspect(config);
    Assert(status.Installed, "The Java agent was not installed.");
    Assert(status.ObsoleteEntries == 0, "An obsolete loader entry remains.");
    Assert(File.Exists(config + ".npcfw-backup"), "The config backup was not created.");

    ProjectZomboidConfig.Remove(config);
    Assert(!ProjectZomboidConfig.Inspect(config).Installed, "The Java agent was not removed.");
}

static void TestSteamDiscovery(string root)
{
    var steamApps = Path.Combine(root, "SteamLibrary", "steamapps");
    var workshop = Path.Combine(steamApps, "workshop", "content", "108600", "test");
    var game = Path.Combine(steamApps, "common", "ProjectZomboid");
    Directory.CreateDirectory(workshop);
    Directory.CreateDirectory(game);
    File.WriteAllText(Path.Combine(steamApps, "appmanifest_108600.acf"), """
        "AppState"
        {
          "appid" "108600"
          "installdir" "ProjectZomboid"
        }
        """);
    var config = Path.Combine(game, "ProjectZomboid64.json");
    File.WriteAllText(config, "{}");

    var installs = GameLocator.FindAll(workshop);
    Assert(installs.Any(install => Path.GetFullPath(install.ConfigPath) == Path.GetFullPath(config)),
        "The containing Steam library was not discovered.");
}

static void TestLlmConfiguration(string root)
{
    var userDirectory = Path.Combine(root, "Zomboid");
    LlmConfigurationStore.SaveOllama(userDirectory, "llama3.2:3b");
    var path = Path.Combine(userDirectory, "NPCFW_LLM_PROVIDER.json");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    Assert(document.RootElement.GetProperty("provider").GetString() == "ollama",
        "The Ollama provider was not saved.");
    Assert(document.RootElement.GetProperty("model").GetString() == "llama3.2:3b",
        "The Ollama model was not saved.");

    var profile = new RemoteApiProfile(
        "https://api.openai.com/v1", "example-model", "secret-key");
    LlmConfigurationStore.SaveRemoteProfile(userDirectory, profile);
    Assert(LlmConfigurationStore.LoadRemoteProfile(userDirectory) == profile,
        "The encrypted remote API profile did not round-trip.");
    LlmConfigurationStore.Disable(userDirectory);
    Assert(!File.Exists(path), "The LLM provider configuration was not removed.");
}

static async Task TestBridgeLifecycle()
{
    await using var bridge = new RemoteApiBridge();
    await bridge.StartAsync("https://api.openai.com/v1", "secret-key");
    Assert(bridge.IsRunning, "The remote API bridge did not start.");
    Assert(bridge.Endpoint.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
        "The remote API bridge is not bound to loopback.");
    await bridge.StopAsync();
    Assert(!bridge.IsRunning, "The remote API bridge did not stop.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
