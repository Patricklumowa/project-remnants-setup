namespace ProjectRemnants.Setup.Voice;

public sealed record VoiceAsset(string FileName, Uri Url, string Sha256, long Size);

public sealed record VoiceModel(string Id, string Title, string Summary, VoiceAsset Asset);

public static class VoiceCatalog
{
    public static VoiceAsset Engine { get; } = new(
        "whisper-bin-x64.zip",
        new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-x64.zip"),
        "f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c",
        8_573_270);

    public static IReadOnlyList<VoiceModel> Models { get; } =
    [
        new VoiceModel(
            "fast",
            "Fast (English, 141 MB)",
            "Quick replies on most PCs. Recommended.",
            new VoiceAsset(
                "ggml-base.en.bin",
                new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin"),
                "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
                147_964_211)),
        new VoiceModel(
            "accurate",
            "Accurate (English, 181 MB)",
            "Better with accents and background noise, but about three times slower.",
            new VoiceAsset(
                "ggml-small.en-q5_1.bin",
                new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q5_1.bin"),
                "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30",
                190_098_681))
    ];

    public static VoiceModel Default => Models[0];
}
