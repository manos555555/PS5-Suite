# Changelog - PS5 Suite

## v7.8.1 (November 2026)

### New — verified unlock cascade (payload)

- **Every trophy now goes through a verified 4-layer cascade** instead of stopping at the first failure: (1) UDS event posts (with the FW-signature fallback), (2) the daemon's own state is queried via the borrowed Trophy2 pair (`GetTrophyInfo`) to see what actually unlocked, (3) `sceNpTrophy2SystemDebugUnlockTrophy` (IPMI) runs on everything still locked — not only on posted ids, (4) a direct `TRPTITLE.DAT` edit pins whatever the daemon still refuses. Previously the debug-unlock and file-edit layers only ran for trophies whose UDS post succeeded, so games where posts fail (custom-protocol titles) got nothing at all.
- **Per-trophy layer report** — the unlock response now ends with `post=N du=N file=N already=N stuck=[ids]`, so a field report shows exactly which layer landed each trophy and which ids remain stuck.
- **Daemon-side facts established by disassembly** (FW 13.60 dump): the post validation rejects `options>=2` with `0x80553102` (FW 5.50 rejects `1`), the (ctx,hnd) pair must be state-consistent or the post fails `0x8055b704`, and the daemon auto-schedules its extraction pass after every accepted post.

### New — registration repair for invisible mounted games (payload)

- **`repair:<title_id|all>` command** — suite-mounted games that are mounted but missing from the home screen can now be re-registered in place: the payload re-ensures `/user/app/<TID>/sce_sys/{param.json,icon0.png}` (copying them from the game source when missing) and re-runs the title registration (`sceAppInstUtilAppInstallTitleDir` path — the recipe proven by Payload Manager's app_installer, GPL-3.0). `repair:all` touches only suite-mounted titles (`mount.lnk` marker) — real PSN/disc installs are never affected. Registration previously ran only on the first mount, so titles mounted by older builds never got their registry entry refreshed.

### Fixed — trophies not unlocking on FW 5.50 (issues #16, #17)

- **PostEvent signature fallback** — the v7.8.0 borrowed-context fix kept posting UDS trophy events with `options=1`, which the FW 5.50 trophy daemon rejects with `0x80553102` (invalid argument). Every handle pair (borrowed game ctx, fresh per-post handle, registered handle) is now probed with `options=1` first (the 13.60+ stat-extraction signature) and automatically falls back to `options=0` — the journaled 7.6.0 signature that 5.50 accepts and awards from.
- **Signature marker in diagnostics** — the unlock log now reports which signature fired (`[id37 unlock=0(o0)]` = option 0 worked), so field reports identify the active path at a glance.
- **activityEnd fallback** — the synthetic `activityEnd` post (custom-event games) uses the same dual-signature probe.
- **Verified on FW 5.50** — tester confirmed trophies unlock through the daemon's own award path (no file edits needed: `file:0ed 1alrdy`).

---

## v7.8.0 (October 2026)

### Mobile UI — responsive layout (Android)

- Multi-column pages now stack vertically on narrow screens (< 700px): System Monitor cards, Live Sensors, Network/Traffic, Memory Editor panes, Trophies list + detail, search/sort toolbars, FPKG field pairs
- Fan Control slider stretches to available width; preset buttons wrap
- PKG install/browse buttons wrap; Convert-to-FPKG fields stack
- Memory and PKG pages get scroll containers on phones — nothing clips off-screen
- Long game/store names now ellipsize instead of clipping
- Same shared layout code on desktop: window resized below 700px gets the adaptive layout too

### Trophies — custom-event game support (payload)

- **Borrowed game context for UDS posts** — trophy events are now posted under the game's own daemon-bound UDS context (read from the libSceNpUniversalDataSystem singleton table) instead of a fresh context. Games that use custom extraction events (e.g. Onimusha) only award trophies when the daemon extracts from the game's registered context; posts on a foreign context were accepted but silently discarded.
- **activityEnd trigger** — after posting trophy events, a synthetic `activityEnd` event is sent on the game's context to kick the daemon's extraction pass. Custom-event games extract at activity boundaries, not on every `sceNpCheckCallback` pump.
- **Singleton scan timing** — the UDS singleton is scanned before `CreateContext`/`CreateHandle` to avoid picking up our own freshly minted handle instead of the game's real one.
- **Fallback chain preserved** — if the game's context is unavailable the code falls back to the original fresh-context path, so standard-event games (which worked before) are unaffected.

---

All notable changes to **PS5 Suite** (client app + on-console payload).

---

## v7.7.0 (October 2026)

### Bug Fixes
- **Trophy timestamps now show local time** — `TRPTITLE.DAT` stores unlock timestamps in UTC (microseconds since 0001-01-01); the client was displaying them as-is without `.ToLocalTime()`, so every trophy appeared unlocked hours behind the actual local time (e.g. 3 hours off in UTC+3 timezones).
- **Download data corruption fixed** — the payload's `handle_download_file()` used a static shared `g_dl_buf` across all threads; concurrent downloads could interleave buffer contents. Each `client_session_t` now has its own lazy-allocated 16 MB `dl_buf`.
- **Trophy unlock/lock guards** — the client no longer issues an unlock on an already-unlocked trophy or a lock on an already-locked one.
- **Unmount All button state** — the button is now re-enabled based on actual connection state after the operation instead of unconditionally.
- **`CMD_UNMOUNT_GAME` without a title id** now returns a proper error instead of being silently ignored.
- **`.bak` backup integrity (payload)** — `trp_lock_file`/`trp_unlock_file` verify the backup copy completed before rewriting `TRPTITLE.DAT`; a partial/failed backup aborts the edit and cleans up.

### Major Improvements
- **Parallel folder downloads (8 connections)** — `DownloadFolderAsync()` replaced the sequential loop with 8 parallel `Task.Run` workers throttled by `SemaphoreSlim`, each opening its own `PS5Protocol` connection.
- **Double-buffered download I/O** — `DownloadFileAsync()` now alternates two `ArrayPool<byte>` buffers: one reads from the network while the other writes to disk, maximizing throughput on gigabit LANs.

### Trophies (payload)
- **NID-first symbol resolution** — new `krpc_resolve_by_name()` resolves remote symbols by NID first (covers NID-only exports like `libSceNpUniversalDataSystem`) with a dlsym-by-name fallback (covers debug exports that have no NID).
- **Events post as game-generated** — UDS events are now posted with `GENERATED_BY_CODEGEN` on a fresh per-post handle; posts on the registered handle or with option 0 were journaled but never reached the daemon's stat-extraction pass.
- **NP callback pump** — `sceNpCheckCallback` is invoked after every post so the daemon drains the event queue and delivers the notification.
- **Borrowed-context handle binding** — when the game's UDS singleton has no live registered handle, the `udsb:` path mints a fresh one and binds it via `RegisterContext`; the handle scan now sweeps the full handle-id range.
- **New direct-award path (`t2u:`)** — awards/re-locks trophies through `sceNpTrophy2SystemDebugUnlockTrophy`/`sceNpTrophy2SystemDebugLockTrophy`, the daemon's own debug op, on the game's registered ctx/hnd pair.
- **Context destroy = journal flush** — the fresh UDS context/handle created for posting is destroyed afterwards, triggering the daemon's extraction pass that grants the award.
- **Richer diagnostics** — context/handle/register return codes and posted-id tracking are included in the response for easier field debugging.

### Removed
- **Dead `ParallelUploader` class** — the multi-connection upload path was unreachable (always fell back to single connection). Removed entirely.

---

## v7.3.0 (October 2026)

### New Features
- **Unmount All button** — removes every mounted game in one action, with confirmation dialog and per-title progress. Only suite-mounted games are touched (verified by `mount.lnk` marker or active `/system_ex/app` overlay) — real installed titles are never affected.

### Major Improvements
- **Full k-stuff compatibility** — every feature previously gated behind etaHEN now works on k-stuff too: mount/unmount, launch game, controller info + lightbar, PKG installs, suspend/resume/kill.
- **Mount/unmount no longer kills the home screen** — on kstuff firmwares 12.00+, game registration now runs through SceShellCore's own `installTitleDir` via an injected code-cave bridge (offsets verified per-firmware). The daemon writes the registry rows and notifies the UI itself, so icons appear and disappear instantly with zero renderer restarts.
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
