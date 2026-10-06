using System.Text;
using System.Text.Json;
using ProjectRemnants.Setup.Benchmark;
using ProjectRemnants.Setup.Install;
using ProjectRemnants.Setup.Llm;
using ProjectRemnants.Setup.Voice;

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
    await TestOllamaWarmup();
    await TestForcedModelBenchmark(root);
    TestGpuDetection();
    TestModelFit();
    TestCatalogParsing();
    TestRemoteModelDiscovery();
    await TestVoiceInstaller(root);
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

static async Task TestOllamaWarmup()
{
    var calls = 0;
    using var handler = new WarmupHandler(async (request, cancellationToken) =>
    {
        calls++;
        Assert(request.Method == HttpMethod.Post &&
            request.RequestUri?.AbsolutePath == "/api/generate",
            "Model warmup did not use Ollama's generate endpoint.");
        using var body = JsonDocument.Parse(
            await request.Content!.ReadAsStreamAsync(cancellationToken));
        Assert(body.RootElement.GetProperty("model").GetString() == "llama3.2:3b" &&
            body.RootElement.GetProperty("stream").GetBoolean() == false &&
            body.RootElement.GetProperty("keep_alive").GetString() == "1h" &&
            !body.RootElement.TryGetProperty("prompt", out _),
            "Model warmup did not send a load-only request.");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"done\":true,\"done_reason\":\"load\"}")
        };
    });
    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/")
    };
    await OllamaDriver.WarmModelAsync(client, " llama3.2:3b ");
    Assert(calls == 1, "Model warmup sent more than one request.");

    using var failureHandler = new WarmupHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"not enough memory\"}")
        }));
    using var failureClient = new HttpClient(failureHandler)
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/")
    };
    try
    {
        await OllamaDriver.WarmModelAsync(failureClient, "llama3.2:3b");
        throw new InvalidOperationException("A failed warmup was reported as ready.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("not enough memory", StringComparison.Ordinal))
    {
    }
}

static async Task TestForcedModelBenchmark(string root)
{
    var pack = BenchmarkPack.Load();
    Assert(pack.Version == "1.0.0" && pack.Cases.Count == 223, "The bundled test pack must contain 223 cases.");
    Assert(pack.Cases.Count(item => item.JavaBypass == "status") == 1 &&
        pack.Cases.Count(item => item.JavaBypass == "stub") == 2,
        "The test pack lost the Java status or stub bypass cases.");
    var chatCalls = 0;
    var bypassCalls = 0;
    using var handler = new WarmupHandler(async (request, cancellationToken) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/show") return JsonReply("{\"details\":{\"quantization_level\":\"Q4_K_M\"}}");
        if (path == "/api/generate") return JsonReply("{\"done\":true}");
        if (path == "/api/ps") return JsonReply(
            "{\"models\":[{\"name\":\"test:tag\",\"size\":1000,\"size_vram\":600}]}");
        Assert(path == "/api/chat", "Benchmark used the wrong model endpoint.");
        var item = pack.Cases[chatCalls++];
        if (item.JavaBypass.Length != 0) bypassCalls++;
        using var sent = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
        var payload = sent.RootElement;
        Assert(payload.GetProperty("model").GetString() == "test:tag" &&
            payload.GetProperty("options").GetProperty("num_ctx").GetInt32() == 4096 &&
            payload.TryGetProperty("format", out _) &&
            payload.GetProperty("messages").EnumerateArray().Last().GetProperty("content")
                .GetString()!.Contains(item.Utterance, StringComparison.Ordinal),
            "A packed production request was not sent to the selected model.");
        if (chatCalls == 2) return JsonReply("not json");
        if (chatCalls == 3) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("unavailable")
        };
        if (chatCalls == 4) await Task.Delay(Timeout.Infinite, cancellationToken);
        return JsonReply(JsonSerializer.Serialize(new
        {
            done = true,
            message = new { content = "{\"dialogue\":\"OK\",\"relationship\":\"SKIP\",\"action\":{\"type\":\"NONE\",\"mode\":\"\",\"actor\":\"\",\"target\":\"\",\"item\":\"\",\"source\":\"\",\"destination\":\"\",\"location\":\"\",\"quantity\":1,\"all\":false,\"deliveries\":[]}}" },
            load_duration = chatCalls == 1 ? 2_000_000_000L : 0L,
            prompt_eval_count = 100, prompt_eval_duration = 1_000_000_000L,
            eval_count = 10, eval_duration = 500_000_000L
        }));
    });
    using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var runner = new OllamaBenchmark(client, TimeSpan.FromMilliseconds(30),
        Path.Combine(root, "benchmark-reports"));
    var report = await runner.RunAsync(pack, "test:tag");
    Assert(chatCalls == 223 && bypassCalls == 3 && report.Cases.Count == 223,
        "Each of the 223 forced-model cases must make a real chat call, including status and stubs.");
    Assert(report.Cases[1].FailureReasons.Any(reason => reason.Contains("Json", StringComparison.OrdinalIgnoreCase)) &&
        report.Cases[2].FailureReasons.Any(reason => reason.Contains("HTTP 503")) &&
        report.Cases[3].FailureReasons.Any(reason => reason.Contains("timed out")),
        "Malformed JSON, HTTP errors and timeouts must retain per-case failures: " +
        string.Join(" | ", report.Cases.Take(4).Select(item => string.Join("; ", item.FailureReasons))));
    Assert(report.ColdLoadMs == 2000 && report.Summary.PromptTokensPerSecond == 100 &&
        report.Summary.GenerationTokensPerSecond == 20 && report.Placement.StartsWith("Mixed") &&
        report.Quantization == "Q4_K_M" && report.ContextSetting == 4096,
        "Ollama nanosecond timing or model metadata was calculated incorrectly.");
    Assert(File.Exists(report.AutoSavePath) && report.Summary.Categories.Count > 0,
        "The detailed report was not saved with category scores.");
    using (var saved = JsonDocument.Parse(File.ReadAllText(report.AutoSavePath)))
        Assert(saved.RootElement.GetProperty("cases").GetArrayLength() == 223,
            "The exported report is incomplete.");

    var cancelledCalls = 0;
    using var cancelHandler = new WarmupHandler((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/chat") cancelledCalls++;
        return Task.FromResult(request.RequestUri.AbsolutePath switch
        {
            "/api/show" => JsonReply("{}"),
            "/api/generate" => JsonReply("{\"done\":true}"),
            "/api/ps" => JsonReply("{\"models\":[]}"),
            _ => JsonReply("{\"done\":true,\"message\":{\"content\":\"{\\\"dialogue\\\":\\\"OK\\\",\\\"relationship\\\":\\\"SKIP\\\",\\\"action\\\":{\\\"type\\\":\\\"NONE\\\"}}\"}}")
        });
    });
    using var cancelClient = new HttpClient(cancelHandler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var cancelRunner = new OllamaBenchmark(cancelClient, reportDirectory: Path.Combine(root, "cancelled-reports"));
    using var cancellation = new CancellationTokenSource();
    var partial = await cancelRunner.RunAsync(pack, "test:tag", new CallbackProgress<BenchmarkReport>(
        update => { if (update.Cases.Count == 1) cancellation.Cancel(); }), cancellation.Token);
    Assert(partial.Cancelled && partial.Cases.Count == 1 && cancelledCalls == 1 &&
        File.Exists(partial.AutoSavePath), "Cancellation lost the partial report or sent another model request.");
}

static HttpResponseMessage JsonReply(string content) => new(System.Net.HttpStatusCode.OK)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json")
};

static async Task TestVoiceInstaller(string root)
{
    var directory = Path.Combine(root, "voice");
    var engineZip = BuildEngineZip();
    var engine = new VoiceAsset(
        "engine.zip", new Uri("https://example.test/engine.zip"), Sha256Hex(engineZip), engineZip.Length);
    var modelBytes = Encoding.ASCII.GetBytes(new string('m', 4096));
    var model = new VoiceModel("test", "Test model", "summary", new VoiceAsset(
        "ggml-test.en.bin", new Uri("https://example.test/ggml-test.en.bin"),
        Sha256Hex(modelBytes), modelBytes.Length));
    var requests = new List<string>();
    var files = new Dictionary<string, byte[]>
    {
        [engine.Url.ToString()] = engineZip,
        [model.Asset.Url.ToString()] = modelBytes
    };
    using var http = new HttpClient(new VoiceHandler(files, requests));
    var installer = new VoiceInstaller(directory, http);
    Assert(!installer.GetStatus().EngineInstalled, "Voice input should start uninstalled.");

    var percents = new List<int>();
    await installer.InstallAsync(model, new CallbackProgress<VoiceProgress>(report =>
    {
        if (report.Percent is { } percent)
        {
            percents.Add(percent);
        }
    }), default, engine);
    var status = installer.GetStatus();
    Assert(status.EngineInstalled, "The speech engine was not installed.");
    Assert(status.InstalledModelFiles.SequenceEqual(["ggml-test.en.bin"]), "The model file was not installed.");
    Assert(status.ActiveModelFile == "ggml-test.en.bin", "The installed model was not selected.");
    Assert(percents.Count > 0 && percents.Max() == 100, "Download progress did not reach 100 percent.");
    var bin = Path.Combine(directory, "bin");
    Assert(File.Exists(Path.Combine(bin, "whisper-server.exe")) && File.Exists(Path.Combine(bin, "whisper.dll"))
        && File.Exists(Path.Combine(bin, "ggml-cpu-x64.dll")), "Engine files are missing.");
    Assert(!File.Exists(Path.Combine(bin, "SDL2.dll")) && !File.Exists(Path.Combine(bin, "whisper-cli.exe")),
        "Unneeded engine files were installed.");
    Assert(!File.Exists(Path.Combine(directory, "escape.dll")) && !File.Exists(Path.Combine(root, "escape.dll")),
        "A zip entry escaped the engine folder.");
    Assert(!Directory.EnumerateFileSystemEntries(directory).Any(path =>
        Path.GetFileName(path).StartsWith(".bin-", StringComparison.Ordinal)), "Staging folders were left behind.");
    Assert(File.ReadAllText(Path.Combine(directory, "speech.properties")).Contains("model=ggml-test.en.bin"),
        "speech.properties does not name the model.");
    Assert(!File.ReadAllBytes(Path.Combine(directory, "speech.properties")).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }),
        "speech.properties has a BOM.");

    var requestsBefore = requests.Count;
    await installer.InstallAsync(model, null, default, engine);
    Assert(requests.Count == requestsBefore, "An installed model and engine were downloaded again.");

    File.WriteAllText(Path.Combine(directory, "speech.properties"), "threads=3\r\nmodel=old.bin\r\n");
    installer.SelectModel(model);
    var config = File.ReadAllText(Path.Combine(directory, "speech.properties"));
    Assert(config.Contains("threads=3") && config.Contains("model=ggml-test.en.bin") && !config.Contains("old.bin"),
        "Selecting a model changed other settings or kept the old model.");

    var corrupt = new VoiceModel("bad", "Bad model", "summary", new VoiceAsset(
        "ggml-bad.en.bin", new Uri("https://example.test/bad.bin"), new string('0', 64), modelBytes.Length));
    files[corrupt.Asset.Url.ToString()] = modelBytes;
    await AssertThrowsAsync<InvalidDataException>(
        () => installer.InstallAsync(corrupt, null, default, engine), "A corrupt download was accepted.");
    Assert(!File.Exists(Path.Combine(directory, "models", "ggml-bad.en.bin"))
        && !File.Exists(Path.Combine(directory, "models", "ggml-bad.en.bin.part")),
        "A corrupt download left files behind.");
    Assert(installer.GetStatus().ActiveModelFile == "ggml-test.en.bin", "A failed install changed the active model.");

    var wrongSize = new VoiceModel("short", "Short model", "summary", new VoiceAsset(
        "ggml-short.en.bin", new Uri("https://example.test/short.bin"), Sha256Hex(modelBytes), modelBytes.Length + 10));
    files[wrongSize.Asset.Url.ToString()] = modelBytes;
    await AssertThrowsAsync<InvalidDataException>(
        () => installer.InstallAsync(wrongSize, null, default, engine), "A download with the wrong size was accepted.");

    installer.Remove();
    Assert(!Directory.Exists(directory), "Remove did not delete the voice folder.");

    var foreign = Path.Combine(root, "voice-foreign");
    Directory.CreateDirectory(foreign);
    File.WriteAllText(Path.Combine(foreign, "keep.txt"), "mine");
    var foreignInstaller = new VoiceInstaller(foreign, http);
    await AssertThrowsAsync<InvalidOperationException>(
        () => foreignInstaller.InstallAsync(model, null, default, engine), "A folder the app does not own was used.");
    Assert(File.Exists(Path.Combine(foreign, "keep.txt")), "A foreign folder was modified.");
    Assert(Throws<InvalidOperationException>(() => foreignInstaller.Remove()), "A foreign folder was removed.");
    Assert(File.Exists(Path.Combine(foreign, "keep.txt")), "A foreign folder lost its files.");

    var missingServer = new VoiceInstaller(Path.Combine(root, "voice-broken"), http);
    var brokenZip = BuildEngineZip(includeServer: false);
    var brokenEngine = new VoiceAsset(
        "broken.zip", new Uri("https://example.test/broken.zip"), Sha256Hex(brokenZip), brokenZip.Length);
    files[brokenEngine.Url.ToString()] = brokenZip;
    await AssertThrowsAsync<InvalidDataException>(
        () => missingServer.InstallAsync(model, null, default, brokenEngine), "An engine package without the server was accepted.");
    Assert(!missingServer.GetStatus().EngineInstalled, "A broken engine package was installed.");
}

static byte[] BuildEngineZip(bool includeServer = true)
{
    using var stream = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(
        stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        var names = new List<string>
        {
            "Release/whisper.dll", "Release/ggml-cpu-x64.dll", "Release/SDL2.dll",
            "Release/whisper-cli.exe", "Release/../escape.dll"
        };
        if (includeServer)
        {
            names.Add("Release/whisper-server.exe");
        }

        foreach (var name in names)
        {
            var entry = zip.CreateEntry(name);
            using var writer = entry.Open();
            writer.Write(Encoding.ASCII.GetBytes("binary " + name));
        }
    }

    return stream.ToArray();
}

static string Sha256Hex(byte[] bytes) =>
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

static bool Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
        return false;
    }
    catch (T)
    {
        return true;
    }
}

static async Task AssertThrowsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException(message);
}


sealed class WarmupHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request, cancellationToken);
}

sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

sealed class VoiceHandler(Dictionary<string, byte[]> files, List<string> requests) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.ToString();
        requests.Add(key);
        if (!files.TryGetValue(key, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                RequestMessage = request
            });
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
            RequestMessage = request
        });
    }
}
