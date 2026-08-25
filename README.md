# InnerTune for Windows

A local-first Windows music player backed by YouTube Music's InnerTube API. The resident UI and audio output use native Windows components; Node.js starts only for search and stream resolution, and Codex CLI starts only for AI requests.

## Install and run

Download the latest `InnerTune-Setup-*.exe` from [GitHub Releases](https://github.com/maramizo/InnerTune/releases/latest) and run it. InnerTune checks GitHub Releases at startup and every 12 hours. Updates are downloaded only when a matching SHA-256 asset is present, then installed with a one-click restart.

InnerTune remains available from the notification area when its window is hidden.

Setup also adds InnerTune to your Windows startup folder so the tray control is available after sign-in.

The installer bundles Node.js and FFmpeg. FFmpeg only performs a lossless container remux when a song is first cached; it does not re-encode the audio or remain running. Codex CLI is optional unless you use **Ask Luna**. The AI model defaults to `gpt-5.6-luna`.

InnerTune discovers Codex from the official Windows or standalone installation, Windows App Paths, refreshed user and machine `PATH`, WinGet/WindowsApps, npm, or pnpm. Portable installations can set `INNERTUNE_CODEX_PATH` to their `codex.exe`.

## Controls

- The queue remains visible while you search, chat, or browse your library.
- **Add** appends a search result without interrupting playback. **Play** or a double-click explicitly starts it.
- Double-click a queued song to play it.
- Use **×** on a queue row to remove it.
- Save queues with paths such as `Focus/Night`; the folders are created automatically.
- Press `:` outside a text field to open Luna. Search appears again only when you select Search.
- Use **Mini player** for the compact always-on-top widget.
- Closing the window hides it to the notification area; use the tray menu to exit.

Library state is stored locally at `%LOCALAPPDATA%\InnerTune\library.json`.

## Isolated UI tests

InnerTune has an environment-gated test mode so UI automation can run beside the normal player without taking over playback or the desktop. Test mode:

- requires a unique `INNERTUNE_TEST_INSTANCE` and a non-production `ITMUSIC_DATA_DIR`;
- uses a separate singleton and activation-pipe namespace;
- isolates the library, discovery cache, provider data, and audio cache;
- forces both song and video output to zero volume;
- creates no tray icon, never activates itself, and positions its window off-screen.

Run the smart-video integration test with:

```powershell
powershell -ExecutionPolicy Bypass -File .\diagnostics\test-smart-video.ps1
```

The harness attaches through UI Automation without moving the physical mouse. It leaves an already-running InnerTune process alone and deletes its disposable data when finished. Add `-KeepTestData` when diagnosing a failure.

## Publish a release locally

Publishing does not require GitHub Actions. Update the version in the project and installer files, then run this from PowerShell on a Windows checkout:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-release.ps1
```

The script builds the installer, creates its SHA-256 sidecar, and creates or updates the matching GitHub Release using your authenticated GitHub CLI session.
