# Project Remnants Setup

Installs the Project Remnants Java agent for Project Zomboid, plus
optional local LLM and voice input.

## Windows

1. Run `ProjectRemnantsSetup.exe`. Nothing to install.
2. **Game Setup** finds your game and `NPCFW.jar` automatically.
   Click **Install / Repair** (or **Full Uninstall** to remove it).
3. Optional tabs: **LLM Companion** (Local Ollama, OpenAI-compatible
   API, or Local llama.cpp) and **Voice Input**.

The same `.exe` also accepts command-line flags, e.g.
`ProjectRemnantsSetup.exe --help`.

## Linux

There is no GUI on Linux. Download the `linux-x64`
`ProjectRemnantsSetup` binary, make it executable, and run it:

```sh
chmod +x ProjectRemnantsSetup
./ProjectRemnantsSetup --detect
./ProjectRemnantsSetup --install /path/to/ProjectZomboid64.json /path/to/NPCFW.jar
```

Optional extras (full list in `--help`):

```sh
./ProjectRemnantsSetup --llm-enable-ollama ~/Zomboid llama3.2:3b
./ProjectRemnantsSetup --llm-enable-llamacpp ~/Zomboid /path/to/model.gguf
./ProjectRemnantsSetup --voice-install fast
./ProjectRemnantsSetup --uninstall /path/to/ProjectZomboid64.json
```

## Building from source

Requires the .NET 10 SDK. Windows GUI:

```powershell
dotnet publish src/ProjectRemnants.Setup -c Release -f net10.0-windows -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Linux single-file binary (no runtime needed on the target machine):

```sh
dotnet publish src/ProjectRemnants.Setup -c Release -f net10.0 -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
```
