param(
    [string]$ModRoot = 'C:\Program Files (x86)\Steam\steamapps\common\ProjectZomboid\ProjectRemnants'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$classes = Join-Path $repo 'src\ProjectRemnants.Setup\obj\pack-exporter'
$output = Join-Path $repo 'src\ProjectRemnants.Setup\Benchmark\forced-model-pack-v1.json'
$pzJar = Join-Path (Split-Path -Parent $ModRoot) 'projectzomboid.jar'
$classPath = "$classes;$ModRoot\build\test-classes;$ModRoot\build\classes;$pzJar;$ModRoot\lib\asm-9.8.jar"
foreach ($pair in @(
    @('tests\npcfw\conversation\LLMActionSimulationBenchmark.java', 'build\test-classes\npcfw\conversation\LLMActionSimulationBenchmark.class'),
    @('src\npcfw\conversation\LLMConversationService.java', 'build\classes\npcfw\conversation\LLMConversationService.class'),
    @('src\npcfw\conversation\LLMActionProposal.java', 'build\classes\npcfw\conversation\LLMActionProposal.class')
)) {
    $source = Get-Item -LiteralPath (Join-Path $ModRoot $pair[0])
    $compiled = Get-Item -LiteralPath (Join-Path $ModRoot $pair[1])
    if ($compiled.LastWriteTimeUtc -lt $source.LastWriteTimeUtc) {
        throw "Compiled mod class is older than its source: $($pair[1]). Build the mod separately, then rerun the exporter."
    }
}
New-Item -ItemType Directory -Force -Path $classes | Out-Null
& javac -cp $classPath -d $classes (Join-Path $PSScriptRoot 'ExportBenchmarkPack.java')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& java -cp $classPath npcfw.conversation.ExportBenchmarkPack $ModRoot $output
exit $LASTEXITCODE
