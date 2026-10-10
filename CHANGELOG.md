# Changelog - PS5 Suite

All notable changes to **PS5 Suite** (client app + on-console payload).

---

## v7.3.0 (October 2026)

### New Features
- **Unmount All button** — removes every mounted game in one action, with confirmation dialog and per-title progress. Only suite-mounted games are touched (verified by `mount.lnk` marker or active `/system_ex/app` overlay) — real installed titles are never affected.

### Major Improvements
- **Full k-stuff compatibility** — every feature previously gated behind etaHEN now works on k-stuff too: mount/unmount, launch game, controller info + lightbar, PKG installs, suspend/resume/kill.
- **Mount/unmount no longer kills the home screen** — on kstuff firmwares 12.00+, game registration now runs through SceShellCore's own `installTitleDir` via an injected code-cave bridge (the mount bridge technique, offsets verified per-firmware). The daemon writes the registry rows and notifies the UI itself, so icons appear and disappear instantly with zero renderer restarts.
- **Unregister via `sceAppInstUtilAppUnInstall`** — official daemon IPC removes the icon live; direct SQLite writes remain only as a watchdog-guarded fallback.
- **Launch Game works on k-stuff** — removed the etaHEN-only gate. `sceLncUtilLaunchApp` error codes are now handled correctly: the daemon launches asynchronously, so the payload polls for the target's `eboot.bin` process (up to ~8s) and reports real success with the actual app_id. If another game holds the foreground, it is closed via `sceLncUtilKillApp` and the launch is retried — same as the PS5 UI's "close game?" flow.
- **Power actions use official daemon paths** — reboot/shutdown no longer call the raw `reboot()` syscall (which halted the kernel but left the console in a zombie state that needed a power-plug pull). Reboot now goes through `sceSystemServiceRequestReboot`; shutdown through `sceSystemStateMgrTurnOff` with `sceShellCoreUtilRequestShutdown`/`RequestPowerOff` fallbacks. Raw syscall kept only as last resort.
- **Controller info + lightbar on k-stuff** — removed the etaHEN gate; the ShellCore/ShellUI remote pad bridge (ptrace + remote calls inside a process that owns a real pad session) works on both loaders. Verified live with real analog/button data.

### Bug Fixes
- Fixed mounted-game icons landing past the visible edge of the home row (`lastAccessIndex` now uses MAX+1 like real installs; stale index-0 rows are healed automatically).
- Fixed `proc_find_name` ENOMEM race that could fail process lookups.
- Stale ShellCore bridge hooks are detected and recovered instead of failing the registration.
- Fixed launch kill-heuristic matching system daemons with numeric title IDs — a running game is now identified strictly by `comm == "eboot.bin"`; system processes (SceCdlgApp, ShellUI) are never touched.
- Trophy unlock/re-lock fixes from v7.2.x verified under k-stuff (fixes issue #11).

### Removed
- **Capture Screen** — removed from payload and both clients. `sceScreenShotCapture` requires a registered app-thread context the payload cannot satisfy: local calls return `0x80C1B001` and remote calls inside ShellUI/ShellCore/capture daemons/`eboot.bin` all return `0x80BE0001`. Rather than a button that can never work, the feature is gone; browsing/downloading/deleting existing captures in the Screenshots tab is unaffected.

---

## v7.2.4 (October 2026)

### Bug Fixes
- **Fixed Windows startup crash** — the self-contained single-file build failed on some systems with `System.DllNotFoundException` (`SkiaSharp.SKImageInfo` / `libSkiaSharp.dll`). `IncludeNativeLibrariesForSelfExtract` is now set permanently in `client-avalonia/PS5Suite.csproj`, so every future publish extracts the native Skia/HarfBuzz/ANGLE libraries correctly. (Fixes issue #10)

### New Features
- **Automatic update checker on startup** — desktop and Android now query the GitHub releases API once per launch and prompt **only** when a strictly newer release exists. *Yes* opens the release page in the browser; offline/rate-limit failures stay silent; no prompt when already on the latest version.
- Android app now reads its version from the assembly (`<Version>7.2.4</Version>`) so the update checker reports correctly.

---

## v7.2.3 (October 2026)

### New Features
- **Android client — full feature parity with desktop**: Files toolbar (upload/download/delete/rename/new folder), SAF-based local browser, Games, Saves, Screenshots, Trophies, Prospero Store, dialogs, live search & sorting, mount/stop game, cover icons, trophy unlock/re-lock.
- **Trophy re-lock** — per-trophy lock via payload-side `TRPTITLE.DAT` rewriting, verified live.
- **Prospero Store** — browse the `homebrew.page` catalogue by category, download + SHA-256 verify, extract, install to `/data/homebrew/<TITLEID>/`, auto-mount.
- **PKG-Zone Homebrew Store** — integrated catalogue with one-tap install.
- **macOS `.app` packaging** — releases ship `PS5Suite.app` inside a ZIP that preserves the Unix executable bit (`0o755`), so no `chmod +x` needed after download. (Fixes issue #8)
- Live search + sorting in Games and Trophies tabs.
- System Info tab shows real values read from the console, with graceful "unavailable" for unsupported fields.

### Bug Fixes
- Fixed false-positive trophy mask detection (tested on Astro Bot).
- Fixed Windows single-file publish missing macOS/Linux packaging conventions.

### Documentation
- Removed all Blu-ray disc-dump references from README and release notes.
- Added trophy-safety disclaimer: unlocking many trophies with unrealistically short/identical timestamps can look suspicious if the console ever syncs to PSN — use offline, at your own risk.

---

## v7.2.x and earlier

Earlier development history (the project evolved from *PS5 Upload Suite* — a file uploader — into the full management suite). Highlights carried forward:

- Parallel high-speed file transfer engine (~280 MB/s aggregate)
- Game mount / unmount / launch / stop with `nullfs` + `sce_sys` handling
- Decrypted save backup/restore and screenshots gallery
- Live hardware monitoring (temps, CPU, RAM), fan control, console LED, DualSense lightbar
- Remote shell, memory read/write tools, kernel log
- PKG streaming install, FPKG conversion, archive upload
- Full filesystem indexing + instant search on console
- Payload self-update from GitHub releases

Full granular history is available in the git log.
