# CLAUDE.md

Remote-Gamehub — a from-scratch GameStream (NVIDIA Shield protocol) host for Windows 11 x64,
compatible with stock Moonlight clients. Sunshine-equivalent, written in C# (.NET 10).

## Target platform

Windows 11 x64 only, discrete NVIDIA or AMD GPUs only (NVENC / AMF). No other OS, architecture,
integrated/Intel GPUs or software encoding — don't add build scripts or code paths for them.

## Build and test

- `./build.ps1` — restore + `dotnet publish -c Release` into `build/Remote-Gamehub.exe`
  (single-file, framework-dependent, needs .NET Desktop Runtime 10). Cleans `bin/`/`obj/` after.
- `dotnet test test/Remote-Gamehub.Tests.csproj -c Release` — xUnit tests (same as CI).
- Single test: `dotnet test test/Remote-Gamehub.Tests.csproj --filter "FullyQualifiedName~ConfFileTests"`
- `./installer/installer.ps1 -Version X.Y` — WiX 6 MSI into `build/Remote-Gamehub.msi` (needs the exe first).
- Diagnostic CLI flags of the exe: `--list-displays`, `--encoder-test`, `--scan-games`,
  `--capture-test`, `install-service`, `uninstall-service`. Output goes to the log.

Never delete `build/` wholesale: the app writes its config, log, DB and certificate next to the exe.

## Release process

Pushing to `main` runs `.github/workflows/release.yml`: tests, then a release **only if**
`<Version>` in `src/Remote-Gamehub.csproj` (strictly `major.minor`, e.g. `1.6`) is higher than the
newest `v*` tag **and** `changelog.md` has a non-empty `## X.Y` section. To release: bump the
version and add the changelog section in the same push. Running `release.yml` by hand
on the already released version rebuilds it: moves the tag to the current commit, replaces the
MSI and the notes of that release. PRs run only `tests.yml`; pushes to other
branches run nothing. `build-latest-changes.yml` (manual) builds the exe of any branch as a download.

## Architecture

One exe, three roles decided in `src/Program.cs` by arguments:

1. **Launcher** (no args, elevated) — installs/starts the Windows service (`ServiceControl`),
   waits for the worker to appear, then exits. Falls back to serving itself if the service fails.
2. **Service** (`--service`, LocalSystem, session 0) — `App/ServiceHost.cs`. Streams nothing;
   supervises and (re)starts the worker in the console session via `App/SessionLauncher.cs`.
   Stops the worker through the global event `WorkerStopEvent` (`App/WorkerStop.cs`).
3. **Worker** (`--worker`, SYSTEM in the user's session) — `Program.Serve()`: the actual server.
   Runs as SYSTEM so it can capture the secure desktop (UAC prompts). Anything that must run as the
   signed-in user (games, browser, HKCU launcher scans) goes through `UserContext.AsConsoleUser`
   / `SessionLauncher.StartAsConsoleUser`.

Source layout (`src/`, namespace `RemoteGameHub.<Folder>`):

- `App/` — config (`AppConfig`, `ConfFile`/`ConfFormat` INI parser), logging (`Log`), service,
  tray icon, preflight checks, autostart (Task Scheduler), update checker.
- `Protocol/` — GameStream: HTTP/HTTPS endpoints (`GameStreamServer`), pairing
  (`PairingManager`, `HostIdentity` cert), RTSP negotiation (`RtspServer`), ENet control stream,
  video RTP + Reed-Solomon FEC, mDNS discovery, UPnP (`PortForwarding`), anti-abuse
  (`AccessGuard`/`BlockStore`), local status/pairing web page (`WebConsole` + `WebAssets`).
  `ClientBackup`: paired devices + the host certificate **with its private key** as one zip
  (`host.pem`, `devices.json`), exported/imported on the page; a restored certificate is written to
  disk and the worker restarts through the service (`WebConsole.Restart`, set only for a worker).
- `Session/` — `StreamSession`/`SessionManager` (one client streaming; built after RTSP ANNOUNCE),
  input injection, gamepads via ViGEmBus, game process watching.
  `HostProgram`: entries with `no_stream` (added by hand on the page) run hidden as the user; stdout
  and stderr go through a pipe into memory (`ProgramOutput`, last 256 KB, shown on the page, never
  on disk; lost if the worker restarts); one application at a time (program or game). Moonlight
  sees it as `currentgame`; launch/resume stream a short notice card (no capture, no display/audio
  change) then end gracefully; `/cancel` kills the process tree. Adopted by a restarted worker via
  the `running_program` table.
  Capture loop (`StreamSession`): frames are sent on the stream's own clock, but when the screen
  runs at the stream's rate a frame that is a moment late is waited for (half an interval) instead
  of the last being sent twice; the `stream:` log line counts those repeats. A removed graphics
  device (`DesktopDuplicator.DeviceLost`) rebuilds capture, shader and encoder on a new device.
  At the end of a stream the session (and its audio capture) is disposed before the sound is moved back.
- `Media/` — DXGI Desktop Duplication capture, colour conversion shaders, NVENC and AMF encoders,
  WASAPI audio + Opus (Concentus), display mode/scale switching.
  `ColourConverter`: HDR is scRGB → BT.2020 PQ (same maths as Sunshine); below Windows' SDR white
  level (`DisplayControl.SdrWhiteNits`) the picture is re-decoded from sRGB to gamma 2.2.
  `ScaleStore` keeps what a stream changed and still owes the screen — the desktop scale and the
  HDR switch (`display_scales`, `display_hdr`) — and both are put back at the next start if a
  shutdown or a lost screen prevented it. `DisplayAdaptation` prefers a refresh rate equal to or a
  multiple of the client's fps and warns in the log when the screen has none (judder).
  `AudioAdaptation` adds "Steam Streaming Speakers" from Steam's own driver (`DiInstallDriverW`)
  when surround is asked for and the device is missing, then restores the default device.
- `Library/` — game scanners (Steam, Xbox, Epic, GOG, EA, Battle.net, folders), SQLite `Database`,
  cover art (Steam store + LizardByte GameDB).
- `Native/` — hand-written P/Invoke. COM/D3D11/NVENC/AMF are called through raw vtable function
  pointers (`Native/Com.cs`), not `[ComImport]` — keep that pattern (`AllowUnsafeBlocks` is on).
- `Web/` — `page.html/css/js` of the status page, **embedded resources** (see csproj).
  Header: the name is the project link, then version, the update-check icon and the host chips
  (`WebConsole.Chip`, icon + tooltip; right-aligned from 800 px); the connection is the second line.
  Buttons are inline SVG icons with a `title`; the bottom bar adds text labels from 800 px. Up to
  40rem the editing dialogs are full-screen, and the page never scrolls under an open dialog.
  Lists (games, paired devices) are rendered by the server and re-fetched, so anything that must
  survive a refresh (file input, dialogs) lives in the static `page.html`.
- `AppParameters.cs` — only constants shared by several classes (identity, ports, project link,
  desktop app id). A constant used by one class lives in that class. Anything a user may change
  belongs in `AppConfig` + `sample.conf` instead.

Runtime files (`Remote-Gamehub.conf/.log/.db/.pem`, `covers/`) live beside the exe or in
`%LOCALAPPDATA%\Remote-Gamehub` for an installed copy (the console user's profile when SYSTEM).

## Conventions

- The product has one name, `Remote-Gamehub`: the project file's name. The exe, files, folders,
  service, task, firewall rules and installer all take it from there (`AppParameters.Identity.Name`,
  `$(ProductName)` in WiX); only namespaces and resource names stay `RemoteGameHub` (C# identifiers).
  Removing what older versions left under other names is the installer's job, not the app's.
- Everything is `internal`; tests reach it via `InternalsVisibleTo`. Don't make types public for tests.
- Every file starts with the `// Copyright © AndreyLysikov` + `SPDX-License-Identifier: Apache-2.0` header.
- Comments: only in C# code, in English, at most 2 lines, just the essence (the *why*), no prose.
- No comments anywhere else: build/installer scripts (`*.ps1`), workflows, csproj, manifest,
  `.wxs`, `.gitignore`/`.gitattributes`, `src/Web/*`. Not comments, keep them:
  `#Requires` in `.ps1`, `<!--part:name-->` markers in `page.html` (parsed by `WebAssets.cs`),
  and the `#` notes in `sample.conf` (user docs, must match `ConfFormat`, checked by a test).
- Version is always two numbers `major.minor` (`1.6`, never `1.6.0`): csproj `<Version>`, tags
  `vX.Y`, changelog `## X.Y`, UI. Only the MSI internally gets `X.Y.0` (Windows Installer needs it).
- Localization: every user-visible text (web page, tray menu, balloons, cards, messages Moonlight
  shows) is English wrapped in `Text.T("...")` (use `{0}` placeholders, not concatenation) or
  `[[...]]` in `src/Web/page.html`/`page.js`, with its Russian in `App/TextRu.cs`. Russian is
  chosen when Windows' UI language is Russian. `TextTests` fails on a missing or unused
  translation; texts built at run time go in its `Dynamic` list. The log stays English.
- Log messages are written for the person reading the log: say what happened and what to do.
  Use `Log.Info/Warn/Error/Event/Crash` appropriately; failures that are not fatal are logged, not thrown.
- Single exe is a promise: new assets become `EmbeddedResource`s, no new native dependencies.
- New config key: property with default in `AppConfig`, read in its `Load`, documented in `sample.conf`.
- LF line endings everywhere (`.gitattributes`).
- `SQLitePCLRaw.bundle_e_sqlite3` is referenced explicitly (3.x, SQLite 3.53) above the 2.1.x that
  `Microsoft.Data.Sqlite` pulls in, to get the newest native SQLite with security fixes.
- Stay on the current .NET LTS (10); move to a new major only after its GA release.

## Monthly dependency check

Last check: **2026-09-27**. At the start of a session, if this date is more than a month old:

1. `dotnet list src/Remote-Gamehub.csproj package --outdated` and the same for
   `test/Remote-Gamehub.Tests.csproj`; update to the latest stable versions (no previews/RCs).
2. Check .NET: latest LTS patch in
   `https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json`;
   change the TFM only for a GA LTS major, and tell the user first.
3. Run the tests and `./build.ps1`; confirm the native SQLite version (`select sqlite_version()`).
   Do not run the exe against the real data in `%LOCALAPPDATA%\Remote-Gamehub`.
4. Update the date above, and report what changed.
- Tests: xUnit, one `*Tests.cs` per unit, method names in `Snake_case_sentences`; use `TestFolder`
  for any files on disk.
- User-facing change → add a bullet to `changelog.md` under the upcoming version.

## Working notes

- The user checks behaviour on a real Windows host and drops its `Remote-Gamehub.log` into the
  repository root (git-ignored) for analysis; read it before guessing.
- Open question (2026-10-11): an HDR stream to the living-room TV (Moonlight, 4K at 50 fps, limited
  range) looks too light and banded even with a native HDR game (Forza Horizon 6). The host side
  was checked by reading (shader, range, NVENC VUI offsets, HDR metadata) and no fault was found;
  not yet compared against another client. The gamma 2.2 step above is right for SDR content in
  HDR and is not the fix for this.
- Not confirmed: the TV client is set to 150 Mbit/s, and at 4K the stream sits at 108–109 Mbit/s
  in detailed scenes, which is where the user sees stutter while the server log shows no loss and
  no repeated frames. Lowering the client's bitrate to 50–60 Mbit/s has not been tried yet.
