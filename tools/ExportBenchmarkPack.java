package npcfw.conversation;

import org.json.JSONArray;
import org.json.JSONObject;

import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;

/** Build-only exporter. The mod is a read-only classpath and fixture source. */
public final class ExportBenchmarkPack {
    private static final String[] FIXTURES = {
        "llm_action_scenarios.json", "llm_action_world_scenarios.json",
        "llm_party_action_scenarios.json", "llm_party_model_expanded_scenarios.json",
        "llm_paraphrase_model_scenarios.json", "llm_200_model_scenarios.json",
        "llm_synonym_resilience_scenarios.json", "llm_gamelog_regression_scenarios.json",
        "llm_new_command_scenarios.json", "llm_ingame_context_scenarios.json"
    };

    private ExportBenchmarkPack() {}

    public static void main(String[] args) throws Exception {
        if (args.length != 2) throw new IllegalArgumentException("Usage: ExportBenchmarkPack <mod-root> <output>");
        Path root = Path.of(args[0]);
        Path fixtureRoot = root.resolve("tests/npcfw/conversation");
        Method snapshot = LLMActionSimulationBenchmark.class.getDeclaredMethod(
            "snapshot", JSONObject.class, String.class);
        snapshot.setAccessible(true);
        JSONArray cases = new JSONArray();
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        for (String file : FIXTURES) {
            byte[] bytes = Files.readAllBytes(fixtureRoot.resolve(file));
            digest.update(bytes);
            JSONArray fixtures = new JSONArray(new String(bytes, StandardCharsets.UTF_8));
            for (Object value : fixtures) {
                JSONObject fixture = (JSONObject) value;
                if (!fixture.optBoolean("force_model", false)
                    && !fixture.getJSONObject("expect").getString("type").equals("NONE")) continue;
                JSONArray utterances = fixture.getJSONArray("utterances");
                for (int i = 0; i < utterances.length(); i++) {
                    String utterance = utterances.getString(i);
                    LLMConversationService.ContextSnapshot context =
                        (LLMConversationService.ContextSnapshot) snapshot.invoke(null, fixture, utterance);
                    JSONObject request = new JSONObject(LLMConversationService.buildActionRequestJson(context, "", ""));
                    if (!fixture.has("scene")) request.getJSONObject("options").put("seed", 42);
                    // Model is selected by the setup app at run time, never by this build machine.
                    request.put("model", "__SELECTED_MODEL__");
                    cases.put(new JSONObject()
                        .put("id", fixture.getString("name") + "#" + (i + 1))
                        .put("name", fixture.getString("name"))
                        .put("utterance", utterance)
                        .put("tags", fixture.getJSONArray("tags"))
                        .put("expect", fixture.getJSONObject("expect"))
                        .put("fixture", fixture)
                        .put("java_bypass", fixture.optString("route").equals("status") ? "status"
                            : !fixture.optString("stub_action").isEmpty() ? "stub" : "")
                        .put("request", request));
                }
            }
        }
        if (cases.length() != 223) throw new IllegalStateException("Expected 223 cases, got " + cases.length());
        for (String file : new String[] {
                "tests/npcfw/conversation/LLMActionSimulationBenchmark.java",
                "src/npcfw/conversation/LLMConversationService.java",
                "src/npcfw/conversation/LLMActionProposal.java"
            }) digest.update(Files.readAllBytes(root.resolve(file)));
        JSONObject pack = new JSONObject()
            .put("version", "1.0.0")
            .put("kind", "forced-model benchmark")
            .put("source_sha256", java.util.HexFormat.of().formatHex(digest.digest()))
            .put("cases", cases);
        Path output = Path.of(args[1]);
        Files.createDirectories(output.getParent());
        Files.writeString(output, pack.toString(2), StandardCharsets.UTF_8);
        System.out.println("Wrote " + cases.length() + " cases to " + output);
    }
}
