# Forced-model test pack

`ExportBenchmarkPack.ps1` reads the mod's ten Java benchmark fixture files and calls its compiled production request builder. It writes `src/ProjectRemnants.Setup/Benchmark/forced-model-pack-v1.json` in this repository. The script writes no files to the mod repository.

From the setup repository, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ExportBenchmarkPack.ps1
```

The build machine needs a JDK, a current mod build, and the local Project Zomboid JAR. The published setup app embeds the generated pack and needs none of them. Bump the pack version in `ExportBenchmarkPack.java` when changing its cases or request format.

The app reports **forced-model benchmark** action extraction scores. It sends all 223 cases to `/api/chat`. The Java `--production --only-model` benchmark lists 223 but sends 220 model requests because one status route and two stub cases bypass the model. Java also applies its parser repairs, item and storage resolution, and simulated world execution; the app's correctness score checks the production JSON envelope, expected action type, and explicit action fields. Its report marks Java assertions it does not score. Speed is reported separately from correctness.

## Windows publish package

The working Desktop package is a framework-dependent single file. It needs the .NET 10 Windows Desktop Runtime, but embeds the benchmark pack and needs no JDK or game JAR. Publish it with:

```powershell
dotnet publish src\ProjectRemnants.Setup\ProjectRemnants.Setup.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish\v0.2.4-benchmark
```

On the development PC, Smart App Control allowed this package while blocking the new unsigned self-contained bundle. This is an observed result for this build and PC; trusted code signing is needed for reliable distribution to protected Windows devices.
