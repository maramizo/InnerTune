# InnerTune for Windows

A local-first Windows music player backed by YouTube Music's InnerTube API. The resident UI and audio output use native Windows components; Node.js starts only for search and stream resolution, and Codex CLI starts only for AI requests.

## Install and run

Download the latest `InnerTune-Setup-*.exe` from [GitHub Releases](https://github.com/maramizo/InnerTune/releases/latest) and run it. InnerTune checks GitHub Releases at startup and every 12 hours. When a newer release has a matching SHA-256 asset, InnerTune downloads it, saves playback state, installs it, and restarts automatically.

InnerTune remains available from the notification area when its window is hidden.

Setup also adds InnerTune to your Windows startup folder so the tray control is available after sign-in.

The installer bundles Node.js and FFmpeg. FFmpeg only performs a lossless container remux when a song is first cached; it does not re-encode the audio or remain running. Codex CLI is optional unless you use **Ask Luna**. The AI model defaults to `gpt-5.6-luna`.

InnerTune discovers Codex from the official Windows or standalone installation, Windows App Paths, refreshed user and machine `PATH`, WinGet/WindowsApps, npm, or pnpm. After an automatic update, the installer and restarted app inherit that freshly rebuilt `PATH`. Portable installations can set `INNERTUNE_CODEX_PATH` to their `codex.exe`.

## Controls

- The queue remains visible while you search, chat, or browse your library.
- **Play** or a double-click starts a song as a standalone **Playing now** session. **Add** then grows it into an ad-hoc queue without interrupting playback.
- **Next** places any song directly after the current one. It takes priority over shuffle, and the remaining shuffled songs still play once each.
- Select an artist name anywhere in the app to open that artist’s InnerTube catalog and browse all available songs.
- Double-click a queued song to play it.
- Use **×** on a queue row to remove it.
- Save queues with paths such as `Focus/Night`; the folders are created automatically.
- **Share** copies a compact `innertune://playlist/...` link. Opening the link in Windows previews and saves the named playlist without replacing the current queue; **Import link** under Saved queues is available for manual pasting.
- Press `:` outside a text field to open Luna. Search appears again only when you select Search.
- Use **Mini player** for the compact always-on-top widget.
- Hover InnerTune on the Windows taskbar for Previous, Play/Pause, and Next. The DJ Cat in the notification area animates with wide hand movement and a translucent equalizer; its gesture speed follows a lightweight local BPM estimate.
- Windows media keys and system media controls show the current title, artist, album, artwork, timeline, and transport controls.
- **Settings** includes Midnight, Graphite, and OLED themes; DJ Cat, Minimal, and custom local icons; an audio-reactive animated DJ Cat toggle; and an opt-in playback resume setting. Automatic playback on startup is off by default.
- The active saved queue is outlined and marked **Playing**. Editing the current queue removes that source marker.
- **Shuffle all** above Saved queues combines every saved queue into one deduplicated, randomized listening session without changing the saved queues themselves.
- Closing the window hides it to the notification area; use the tray menu to exit.

Library state is stored locally at `%LOCALAPPDATA%\InnerTune\library.json`. Frequently changing playback position is kept separately in the small `%LOCALAPPDATA%\InnerTune\playback.json`, so routine progress saves do not rewrite the library.

Shared links contain the playlist name and playable track metadata only. They do not contain account data, local paths, favorites, listening history, or playback state.

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

Capture the responsive mini player through WPF's off-screen renderer with:

```powershell
powershell -ExecutionPolicy Bypass -File .\diagnostics\capture-mini-hidden.ps1 `
  -ApplicationPath "$env:LOCALAPPDATA\Programs\InnerTune\InnerTune.exe" `
  -LibraryPath "$env:LOCALAPPDATA\InnerTune\library.json"
```

Capture the Settings page or measure three full/mini transitions without touching the running app:

```powershell
powershell -ExecutionPolicy Bypass -File .\diagnostics\capture-settings-hidden.ps1 `
  -ApplicationPath "$env:LOCALAPPDATA\Programs\InnerTune\InnerTune.exe"

powershell -ExecutionPolicy Bypass -File .\diagnostics\test-mini-memory.ps1 `
  -ApplicationPath "$env:LOCALAPPDATA\Programs\InnerTune\InnerTune.exe" `
  -LibraryPath "$env:LOCALAPPDATA\InnerTune\library.json"

powershell -ExecutionPolicy Bypass -File .\diagnostics\test-queue-flow.ps1 `
  -ApplicationPath "$env:LOCALAPPDATA\Programs\InnerTune\InnerTune.exe"
```

The harness attaches through UI Automation without moving the physical mouse. It leaves an already-running InnerTune process alone and deletes its disposable data when finished.

## Publish a release locally

Publishing does not require GitHub Actions. Update the version in the project and installer files, then run this from PowerShell on a Windows checkout:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-release.ps1
```

The script builds the installer, creates its SHA-256 sidecar, and creates or updates the matching GitHub Release using your authenticated GitHub CLI session.
