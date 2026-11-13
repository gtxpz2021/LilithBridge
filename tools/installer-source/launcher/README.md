# Single-file launcher

The launcher contains the matching small installer ZIP as an embedded resource.
It hashes the entire archive and verifies every extracted file before opening
the ordinary installer with `--resource-dir` pointing at the launcher's original
directory. It writes only its own per-payload cache at this stage, never a game
directory. It does not download arbitrary executable code.

Build the inner installer with `Build-Installer.ps1`. Assemble the small release
ZIP with its `setup` directory and notices, then compile the launcher:

```powershell
./Build-SingleFile.ps1 -Payload ./LilithBridge-installer-current.zip -Destination ./LilithBridge-Setup.exe
```

`Build-SingleFile.ps1` generates the exact payload hash and byte count as C#
constants. The supplied generated identity documents the prepared release ZIP;
regenerate it whenever that ZIP changes. The launcher only prepares files and
opens the existing wizard. Component selection, cached resources, pinned
downloads, stopped-game checks and protected configuration commits stay in
the installer. The 50 MiB online-update ZIP cap is unchanged.

This candidate was compiled only. The launcher, handoff, network retry and
install/upgrade paths were not run; the user owns runtime verification.
