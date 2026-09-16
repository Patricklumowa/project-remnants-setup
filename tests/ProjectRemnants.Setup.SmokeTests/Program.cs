using System.Text;
using System.Text.Json;
using ProjectRemnants.Setup.Install;
using ProjectRemnants.Setup.Llm;

var root = Path.Combine(Path.GetTempPath(), $"ProjectRemnantsSetup-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    TestConfigInstall(root);
    TestLegacyUninstall(root);
    TestUnmarkedLegacyDllCleanup(root);
    TestSteamDiscovery(root);
    TestLlmConfiguration(root);
    await TestOllamaModelDetection(root);
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
    CreateJavaRuntime(game);
    var originalJava = Path.Combine(game, "java.dll");
    File.WriteAllText(originalJava, "original-root-java");
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
    Assert(File.ReadAllText(originalJava) == "runtime-java.dll", "A Java runtime DLL was not copied.");
    Assert(File.Exists(Path.Combine(game, "instrument.dll")), "A missing Java runtime DLL was not copied.");
    Assert(File.Exists(Path.Combine(game, ".project-remnants-files.json")),
        "The copied-file manifest was not created.");
    ProjectZomboidConfig.Install(config, agent);

    ProjectZomboidConfig.Uninstall(config);
    var restored = ProjectZomboidConfig.Inspect(config);
    Assert(!restored.Installed && restored.ObsoleteEntries == 0,
        "An NPCFW loader entry remains in the restored config.");
    Assert(File.ReadAllText(config).Contains("-Xmx4096m", StringComparison.Ordinal),
        "An unrelated game setting was not restored.");
    Assert(!File.Exists(config + ".npcfw-backup"), "The config backup was not removed.");
    Assert(File.ReadAllText(originalJava) == "original-root-java", "A replaced DLL was not restored.");
    Assert(!File.Exists(Path.Combine(game, "instrument.dll")), "A copied DLL was not removed.");
    Assert(!File.Exists(Path.Combine(game, ".project-remnants-files.json")),
        "The copied-file manifest was not removed.");
    Assert(!Directory.Exists(Path.Combine(game, ".project-remnants-backups")),
        "The DLL backup directory was not removed.");

    var fallback = Path.Combine(game, "ProjectZomboid64-fallback.json");
    File.WriteAllText(fallback, """
        {
          "vmArgs": ["-javaagent:C:/Workshop/NPCFW.jar", "-Xmx4096m"]
        }
        """);
    ProjectZomboidConfig.Uninstall(fallback);
    Assert(!ProjectZomboidConfig.Inspect(fallback).Installed,
        "The Java agent was not removed without a backup.");
    Assert(!File.Exists(fallback + ".npcfw-backup"),
        "Uninstall created a new config backup.");
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
    var agent = Path.Combine(workshop, "mods", "ProjectRemnants", "NPCFW.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(agent)!);
    File.WriteAllText(agent, "test");

    var installs = GameLocator.FindAll(workshop);
    Assert(installs.Any(install => Path.GetFullPath(install.ConfigPath) == Path.GetFullPath(config)),
        "The containing Steam library was not discovered.");
    var externalSetup = Path.Combine(Path.GetTempPath(), $"Setup-{Guid.NewGuid():N}");
    Assert(AgentLocator.Find(externalSetup, [game]) == agent,
        "The Workshop NPCFW.jar was not discovered from the game install.");
}

static void TestUnmarkedLegacyDllCleanup(string root)
{
    var game = Path.Combine(root, "unmarked-legacy-game");
    Directory.CreateDirectory(game);
    CreateJavaRuntime(game);
    var config = Path.Combine(game, "ProjectZomboid64.json");
    File.WriteAllText(config, """
        {
          "vmArgs": ["-javaagent:C:/Legacy/NPCFW.jar"]
        }
        """);
    var bin = Path.Combine(game, "jre64", "bin");
    File.Copy(Path.Combine(bin, "instrument.dll"), Path.Combine(game, "instrument.dll"));
    File.Copy(Path.Combine(bin, "java.dll"), Path.Combine(game, "java.dll"));
    File.Copy(Path.Combine(bin, "jli.dll"), Path.Combine(game, "jli.dll"));
    File.Copy(Path.Combine(bin, "server", "jvm.dll"), Path.Combine(game, "jvm.dll"));

    ProjectZomboidConfig.Uninstall(config);
    Assert(!File.Exists(Path.Combine(game, "instrument.dll")),
        "An unmarked legacy DLL copy remains.");
    Assert(!File.Exists(Path.Combine(game, "jvm.dll")),
        "An unmarked legacy JVM copy remains.");
}

static void TestLegacyUninstall(string root)
{
    var game = Path.Combine(root, "legacy-game");
    Directory.CreateDirectory(game);
    CreateJavaRuntime(game);
    var config = Path.Combine(game, "ProjectZomboid64.json");
    File.WriteAllText(config, """
        {
          "vmArgs": ["-javaagent:C:/Legacy/NPCFW.jar", "-Xmx4096m"]
        }
        """);
    var legacyBackup = config + ".ProjectRemnantsBackup.20250101-000000";
    File.WriteAllText(legacyBackup, """
        {
          "vmArgs": ["-Xmx4096m"]
        }
        """);

    var bin = Path.Combine(game, "jre64", "bin");
    File.Copy(Path.Combine(bin, "instrument.dll"), Path.Combine(game, "instrument.dll"));
    File.Copy(Path.Combine(bin, "java.dll"), Path.Combine(game, "java.dll"));
    var javaBackup = Path.Combine(game, "java.dll.ProjectRemnantsBackup.20250101-000000");
    File.WriteAllText(javaBackup, "original-java");
    File.WriteAllText(Path.Combine(game, "NPCFW_combat_log.txt"), "log");
    File.WriteAllText(Path.Combine(game, "unrelated.txt"), "keep");
    var driveRecords = Path.Combine(game, "NPCFW_drive_records");
    Directory.CreateDirectory(driveRecords);
    File.WriteAllText(Path.Combine(driveRecords, "record.csv"), "data");

    ProjectZomboidConfig.Uninstall(config);
    Assert(!ProjectZomboidConfig.Inspect(config).Installed, "The legacy Java agent remains.");
    Assert(!File.Exists(legacyBackup), "The legacy JSON backup remains.");
    Assert(!File.Exists(Path.Combine(game, "instrument.dll")), "A legacy copied DLL remains.");
    Assert(File.ReadAllText(Path.Combine(game, "java.dll")) == "original-java",
        "A legacy DLL backup was not restored.");
    Assert(!File.Exists(javaBackup), "A legacy DLL backup remains.");
    Assert(!File.Exists(Path.Combine(game, "NPCFW_combat_log.txt")), "A legacy log remains.");
    Assert(!Directory.Exists(driveRecords), "The driving-record directory remains.");
    Assert(File.Exists(Path.Combine(game, "unrelated.txt")), "An unrelated game file was removed.");
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
    Assert(LlmConfigurationStore.LoadProvider(userDirectory) ==
        new LlmProviderConfiguration("ollama", "llama3.2:3b"),
        "The active LLM provider did not load.");

    var profile = new RemoteApiProfile(
        "https://api.openai.com/v1", "example-model", "secret-key");
    LlmConfigurationStore.SaveRemoteProfile(userDirectory, profile);
    Assert(LlmConfigurationStore.LoadRemoteProfile(userDirectory) == profile,
        "The encrypted remote API profile did not round-trip.");
    LlmConfigurationStore.Disable(userDirectory);
    Assert(!File.Exists(path), "The LLM provider configuration was not removed.");
    LlmConfigurationStore.SaveOllama(userDirectory, "llama3.2:3b");
    var save = Path.Combine(userDirectory, "Saves", "Sandbox", "World");
    Directory.CreateDirectory(save);
    File.WriteAllText(Path.Combine(save, "NPCFW_Data.bin"), "npc");
    File.WriteAllText(Path.Combine(save, "NPCFW_Safehouses.bin"), "safehouse");
    File.WriteAllText(Path.Combine(save, "map.bin"), "keep");
    File.WriteAllText(Path.Combine(userDirectory, "NPCFW_vehicle_entry_debug.log"), "log");
    var localMod = Path.Combine(userDirectory, "mods", "ProjectRemnants");
    var unrelatedMod = Path.Combine(userDirectory, "mods", "OtherMod");
    Directory.CreateDirectory(localMod);
    Directory.CreateDirectory(unrelatedMod);
    File.WriteAllText(Path.Combine(localMod, "NPCFW.jar"), "agent");
    LlmConfigurationStore.Uninstall(userDirectory);
    Assert(!File.Exists(path), "The provider file remains after uninstall.");
    Assert(!File.Exists(Path.Combine(userDirectory, "NPCFW_LLM_MODEL.txt")),
        "The legacy model file remains after uninstall.");
    Assert(!File.Exists(Path.Combine(userDirectory, "NPCFW_LLM_API_PROFILE.json")),
        "The encrypted API profile remains after uninstall.");
    Assert(!File.Exists(Path.Combine(save, "NPCFW_Data.bin")), "NPC save data remains.");
    Assert(!File.Exists(Path.Combine(save, "NPCFW_Safehouses.bin")), "Safehouse save data remains.");
    Assert(File.Exists(Path.Combine(save, "map.bin")), "Unrelated save data was removed.");
    Assert(!File.Exists(Path.Combine(userDirectory, "NPCFW_vehicle_entry_debug.log")),
        "A user-directory debug log remains.");
    Assert(!Directory.Exists(localMod), "The legacy local mod remains.");
    Assert(Directory.Exists(unrelatedMod), "An unrelated local mod was removed.");
}

static async Task TestOllamaModelDetection(string root)
{
    var models = Path.Combine(root, "OllamaModels");
    var libraryModel = Path.Combine(
        models, "manifests", "registry.ollama.ai", "library", "llama3.2", "3b");
    var customModel = Path.Combine(
        models, "manifests", "registry.ollama.ai", "example", "npc", "latest");
    Directory.CreateDirectory(Path.GetDirectoryName(libraryModel)!);
    Directory.CreateDirectory(Path.GetDirectoryName(customModel)!);
    File.WriteAllText(libraryModel, "{}");
    File.WriteAllText(customModel, "{}");

    await using var ollama = new OllamaDriver();
    var detected = ollama.GetDownloadedModels(models);
    Assert(detected.SequenceEqual(["example/npc:latest", "llama3.2:3b"]),
        "Downloaded Ollama models were not detected.");
    Assert(OllamaDriver.RecommendModels(8).SequenceEqual(
        ["qwen3:8b", "qwen3:4b", "llama3.2:3b"]),
        "The high-end recommendations are wrong.");
    Assert(OllamaDriver.RecommendModels(4).SequenceEqual(
        ["llama3.2:3b", "qwen3:4b"]),
        "The 4 GB recommendations are wrong.");
    Assert(OllamaDriver.RecommendModels(2).SequenceEqual(["llama3.2:1b"]),
        "The fallback recommendation is wrong.");
    Assert(OllamaDriver.GetProgressPercent("pulling model 47% 1.2 GB/2.5 GB") == 47,
        "The Ollama download percentage was not parsed.");
    Assert(OllamaDriver.GetProgressPercent("pulling manifest") is null,
        "A non-progress Ollama line produced a percentage.");
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

static void CreateJavaRuntime(string game)
{
    var bin = Path.Combine(game, "jre64", "bin");
    var server = Path.Combine(bin, "server");
    Directory.CreateDirectory(server);
    foreach (var name in new[]
    {
        "instrument.dll",
        "java.dll",
        "jli.dll",
        "vcruntime140.dll",
        "api-ms-win-crt-convert-l1-1-0.dll",
        "api-ms-win-crt-heap-l1-1-0.dll",
        "api-ms-win-crt-runtime-l1-1-0.dll",
        "api-ms-win-crt-stdio-l1-1-0.dll",
        "api-ms-win-crt-string-l1-1-0.dll"
    })
    {
        File.WriteAllText(Path.Combine(bin, name), $"runtime-{name}");
    }

    File.WriteAllText(Path.Combine(server, "jvm.dll"), "runtime-jvm.dll");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
