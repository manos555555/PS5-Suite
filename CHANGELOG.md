# Changelog - PS5 Suite

All notable changes to **PS5 Suite** (client app + on-console payload).

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
