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
    TestGpuDetection();
    TestModelFit();
    TestCatalogParsing();
    TestRemoteModelDiscovery();
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
        "The 8 GB recommendations are wrong.");
    Assert(OllamaDriver.RecommendModels(4).SequenceEqual(
        ["qwen3:4b", "llama3.2:3b", "llama3.2:1b"]),
        "The 4 GB recommendations are wrong.");
    Assert(OllamaDriver.RecommendModels(2).SequenceEqual(["llama3.2:1b"]),
        "The fallback recommendation is wrong.");
    Assert(OllamaDriver.GetProgressPercent("pulling model 47% 1.2 GB/2.5 GB") == 47,
        "The Ollama download percentage was not parsed.");
    Assert(OllamaDriver.GetProgressPercent("pulling manifest") is null,
        "A non-progress Ollama line produced a percentage.");
}

static void TestGpuDetection()
{
    Assert(OllamaDriver.Classify("NVIDIA GeForce RTX 5070 Laptop GPU") ==
        GpuAcceleration.NvidiaCuda, "An NVIDIA GPU was not classified as CUDA.");
    Assert(OllamaDriver.Classify("AMD Radeon(TM) 610M", discrete: false) ==
        GpuAcceleration.AmdRocm, "An AMD GPU was not classified as ROCm.");
    Assert(OllamaDriver.Classify("Intel(R) UHD Graphics", discrete: false) ==
        GpuAcceleration.None, "An integrated non-AMD/NVIDIA GPU was not treated as unsupported.");

    var hybrid = OllamaDriver.DescribePreferredGpu(
    [
        new GpuAdapter("AMD Radeon(TM) 610M", 0.5, Discrete: false),
        new GpuAdapter("NVIDIA GeForce RTX 5070 Laptop GPU", 7.96, Discrete: true)
    ]);
    Assert(hybrid.Acceleration == GpuAcceleration.NvidiaCuda,
        "The discrete NVIDIA GPU was not preferred.");
    Assert(hybrid.Name == "NVIDIA GeForce RTX 5070 Laptop GPU",
        "The wrong GPU was reported as preferred.");
    Assert(hybrid.Summary == "Preferred GPU: NVIDIA GeForce RTX 5070 Laptop GPU",
        "The preferred GPU summary text is wrong.");

    var only = OllamaDriver.DescribePreferredGpu(
        [new GpuAdapter("Intel(R) Arc(TM) Graphics", 4, Discrete: true)]);
    Assert(only.Summary == "Only GPU: Intel(R) Arc(TM) Graphics",
        "A single GPU was not reported as the only GPU.");

    var none = OllamaDriver.DescribePreferredGpu([]);
    Assert(none.Acceleration == GpuAcceleration.None && none.Summary == "No GPU detected",
        "A GPU-less machine was not handled.");
}

static void TestModelFit()
{
    Assert(ModelFit.TryParseParametersB("7b", out var seven) && Math.Abs(seven - 7) < 0.001,
        "A plain billion size was not parsed.");
    Assert(ModelFit.TryParseParametersB("3.8b", out var threePointEight) &&
        Math.Abs(threePointEight - 3.8) < 0.001, "A fractional billion size was not parsed.");
    Assert(ModelFit.TryParseParametersB("8x22b", out var mixture) &&
        Math.Abs(mixture - 176) < 0.001, "A mixture-of-experts size was not parsed.");
    Assert(ModelFit.TryParseParametersB("335m", out var million) &&
        Math.Abs(million - 0.335) < 0.001, "A million-parameter size was not parsed.");
    Assert(!ModelFit.TryParseParametersB("tools", out _),
        "A capability was parsed as a size.");

    Assert(ModelFit.Evaluate(ModelFit.EstimateDownloadGb(8), 7.96, 16) == Runability.Gpu,
        "An 8B model did not fit an 8 GB GPU.");
    Assert(ModelFit.Evaluate(ModelFit.EstimateDownloadGb(176), 7.96, 16) == Runability.TooBig,
        "A 176B mixture was wrongly judged runnable.");
    Assert(ModelFit.Evaluate(ModelFit.EstimateDownloadGb(8), 0, 32) == Runability.CpuOnly,
        "A GPU-less PC with enough RAM was not CPU-only.");
    Assert(ModelFit.Evaluate(ModelFit.EstimateDownloadGb(8), 0, 4) == Runability.TooBig,
        "A model larger than RAM was wrongly judged runnable.");
    Assert(ModelFit.Evaluate(ModelFit.EstimateDownloadGb(120), 7.96, 32) == Runability.TooBig,
        "A model too large for the PC was judged runnable because it is also on cloud.");

    var recommended = OllamaDriver.RecommendModels(7.96);
    Assert(recommended.Contains("qwen3:8b"), "An 8 GB GPU did not recommend an 8B model.");

    Assert(OllamaDriver.RecommendationTip(7.96).Contains("qwen3:8b"),
        "The 8 GB GPU tip did not suggest an 8B model.");
    Assert(OllamaDriver.RecommendationTip(7.96).Contains("8 GB"),
        "The tip did not report the GPU size.");
    Assert(OllamaDriver.RecommendationTip(0).Contains("no GPU"),
        "A GPU-less PC tip did not mention the CPU fallback.");
    Assert(!recommended.SequenceEqual(["llama3.2:1b"]), "An 8 GB GPU fell back to the tiny model.");
    Assert(OllamaDriver.RecommendModels(0.5).SequenceEqual(["llama3.2:1b"]),
        "A tiny GPU did not fall back to the smallest model.");
}

static void TestCatalogParsing()
{
    const string html = """
        <ul role="list" class="grid grid-cols-1 gap-y-3">
          <li  class="flex items-baseline border-b border-neutral-200 py-6">
            <a href="/library/llama3.2" class="group w-full space-y-5">
              <p class="max-w-lg break-words text-neutral-800 text-md">Meta&#39;s Llama 3.2 goes small.</p>
              <span class="x">tools</span>
              <span class="x">1b</span>
              <span class="x">3b</span>
            </a>
          </li>
          <li  class="flex items-baseline border-b border-neutral-200 py-6">
            <a href="/library/gemma3" class="group w-full space-y-5">
              <p class="max-w-lg break-words text-neutral-800 text-md">Most capable model.</p>
              <span class="x">vision</span>
              <span class="x">4b</span>
            </a>
          </li>
          <li  class="flex items-baseline border-b border-neutral-200 py-6">
            <a href="/library/nomic-embed-text" class="group w-full space-y-5">
              <p class="max-w-lg break-words text-neutral-800 text-md">An embedding model.</p>
              <span class="x">embedding</span>
            </a>
          </li>
        </ul>
        """;

    var models = OllamaCatalog.Parse(html);
    Assert(models.Count == 3, $"Expected 3 catalogue models, got {models.Count}.");
    var llama = models.Single(model => model.Name == "llama3.2");
    Assert(llama.Description == "Meta's Llama 3.2 goes small.",
        "The HTML-encoded description was not decoded.");
    Assert(llama.Capabilities.Contains("tools"), "The model capability was not captured.");
    Assert(llama.Tags.Select(tag => tag.Tag).SequenceEqual(["1b", "3b"]),
        "The model size tags were not captured in order.");
    Assert(llama.DownloadNames.SequenceEqual(["llama3.2:1b", "llama3.2:3b"]),
        "The downloadable tag names are wrong.");
    var embed = models.Single(model => model.Name == "nomic-embed-text");
    Assert(embed.Tags.Count == 0 && embed.DownloadNames.SequenceEqual(["nomic-embed-text"]),
        "A size-less model should download under its bare name.");
}

static void TestRemoteModelDiscovery()
{
    Assert(RemoteModelDiscovery.BuildModelsUri("https://api.openai.com/v1").AbsoluteUri ==
        "https://api.openai.com/v1/models", "A base endpoint did not map to /models.");
    Assert(RemoteModelDiscovery.BuildModelsUri("https://api.openai.com/v1/").AbsoluteUri ==
        "https://api.openai.com/v1/models", "A trailing slash was not handled.");
    Assert(RemoteModelDiscovery.BuildModelsUri(
        "https://api.example.com/v1/chat/completions").AbsoluteUri ==
        "https://api.example.com/v1/models", "A chat-completions URL did not map to /models.");
    Assert(RemoteModelDiscovery.BuildModelsUri(
        "https://api.example.com/v1/models").AbsoluteUri ==
        "https://api.example.com/v1/models", "A /models URL was double-appended.");

    var openAiShape = RemoteModelDiscovery.Parse(
        """
        {"object":"list","data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"gpt-4o"}]}
        """);
    Assert(openAiShape.SequenceEqual(["gpt-4o", "gpt-4o-mini"]),
        "The OpenAI model list was not parsed and de-duplicated.");

    Assert(RemoteModelDiscovery.Parse("""["a","b"]""").SequenceEqual(["a", "b"]),
        "A bare id array was not parsed.");
    Assert(RemoteModelDiscovery.Parse("""[{"id":"m1"},{"id":"m2"}]""").SequenceEqual(["m1", "m2"]),
        "A bare object array was not parsed.");
    Assert(RemoteModelDiscovery.Parse("not json").Count == 0, "Invalid JSON produced models.");
    Assert(RemoteModelDiscovery.Parse("").Count == 0, "Empty input produced models.");
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
