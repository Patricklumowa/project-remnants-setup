# Forced-model test pack

`ExportBenchmarkPack.ps1` reads the mod's ten Java benchmark fixture files and calls its compiled production request builder. It writes `src/ProjectRemnants.Setup/Benchmark/forced-model-pack-v1.json` in this repository. The script writes no files to the mod repository.

From the setup repository, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ExportBenchmarkPack.ps1
```

The build machine needs a JDK, a current mod build, and the local Project Zomboid JAR. The published setup app embeds the generated pack and needs none of them. Bump the pack version in `ExportBenchmarkPack.java` when changing its cases or request format.

The app reports **forced-model benchmark** action extraction scores. It sends all 223 cases to `/api/chat`. The Java `--production --only-model` benchmark lists 223 but sends 220 model requests because one status route and two stub cases bypass the model. Java also applies its parser repairs, item and storage resolution, and simulated world execution; the app's correctness score checks the production JSON envelope, expected action type, and explicit action fields. Its report marks Java assertions it does not score. Speed is reported separately from correctness.

## Windows publish package

The Desktop and release package is a self-contained single file. It includes the .NET 10 Windows Desktop Runtime and the benchmark pack, so users need no separate runtime, JDK, or game JAR. Publish it with:

```powershell
dotnet publish src\ProjectRemnants.Setup\ProjectRemnants.Setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\v0.2.3-benchmark
```

On the development PC, Smart App Control blocks this unsigned self-contained bundle. Trusted code signing is needed for reliable distribution to protected Windows devices.
