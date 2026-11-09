# 🎮 PS5 Suite v8.1.1 — Complete PS5 Management Platform

**By Manos**

<div align="center">

### ☕ Enjoying PS5 Suite?

**This project is built for the PS5 scene, for free — if it saved you time or you just like what we do, consider buying us a coffee. Every cup goes straight back into new features, fixes and payload wizardry.** 🍻

**Got questions, ideas or found a bug? Join the community on Discord — we hang out there!**

[![Ko-fi](https://img.shields.io/badge/Ko--fi-FF5E5B?style=for-the-badge&logo=ko-fi&logoColor=white)](https://ko-fi.com/manosmourtzakis)
[![Follow on X](https://img.shields.io/badge/Follow%20on%20X-000000?style=for-the-badge&logo=x&logoColor=white)](https://x.com/manos_moyrtzis)
[![Join our Discord](https://img.shields.io/badge/Join%20our%20Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/eRXG2Csmpy)

</div>

---

All-in-one management suite for jailbroken PS5 consoles. One app — on **Windows, Linux, macOS and Android** — that does everything: high-speed file transfers, game mount/launch, **trophy unlock**, decrypted save backup/restore, screenshots, live hardware monitoring, PKG streaming installs, full FPKG building on the PC, an integrated Homebrew Store + Prospero Store, memory tools, fan control, kernel log, a remote shell — plus console LED control and DualSense lightbar support. And now: **full Remote Play, built into the app itself** — pair, stream and control the console with video/audio/input running inside PS5 Suite, no second program.

![PS5 Suite — File Transfer](screenshots/file_transfer.png)

## 📸 Screenshots

| | |
|---|---|
| ![File Transfer](screenshots/file_transfer.png) | ![Games](screenshots/games.png) |
| **File Transfer** — dual PC/PS5 browser, storage bar | **Games** — list, mount/launch context menu |
| ![Saves](screenshots/saves.png) | ![System Monitor](screenshots/system_monitor.png) |
| **Saves & Media** — decrypted mount/backup/restore | **System Info** — live sensors, per-core CPU, RAM |
| ![Shell](screenshots/shell.png) | ![Debug Log](screenshots/debug_log.png) |
| **Shell** — remote terminal on the console | **Debug Log** — every operation, timestamped |

---

## 📥 What's in this release

| Asset | Platform |
|---|---|
| `PS5Suite.exe` | Windows 10/11 64-bit — self-contained, no .NET install needed |
| `PS5Suite-Linux-x64.zip` | Linux 64-bit — unzip, `chmod +x ./PS5Suite` and run |
| `PS5Suite-Linux-ARM64.zip` | ARM64 Linux (Raspberry Pi, etc.) — same steps |
| `PS5Suite-macOS-x64.zip` | Intel Macs — unzip, `chmod +x ./PS5Suite`, `xattr -dr com.apple.quarantine ./PS5Suite` |
| `PS5Suite-macOS-ARM64.zip` | Apple Silicon (M1/M2/M3/M4) — same steps |
| `PS5Suite.apk` | **Android** — signed APK, full feature parity with desktop |
| `ps5_suite_server.elf` | **Required** on-console server component |

### ✨ New in v8.1.1

- **Android Remote Play over the internet** — hardware-decoded H.264 video and Opus audio stream to your phone from anywhere; immersive landscape fullscreen with on-screen controller.
- **Suite Link remote connectivity** — 8-character per-console code connects the app from mobile data or any Wi-Fi: direct IPv6 → NAT punch → MQTT relay fallback. No VPN, no port forwarding needed (IPv6 pinhole optional for the fastest path — see guide below).
- **Set / Read Suite Link code** — "Set on PS5" writes a custom code to the console, "Read from PS5" fetches the current one. Validation: 8 chars, A–Z (no I/O), digits 2–9.
- **Adaptive quality** — direct connections (LAN/Wi-Fi/IPv6) negotiate up to 720p60 / 6 Mbps; relay/tunnel paths keep a compact 540p for stability. Full 16:9 frame visible on tall phone screens.
- **File browser Move/Copy with folder picker** — moves and copies now open a remote folder picker (browse PS5 dirs, ".." up, optional rename in the same dialog) instead of a free-text path input. Live progress strip during cross-mount copies (progress bar + `Moving X/Y MB`); same-mount moves complete instantly via `rename()`.
- **Trophy Lock removed** — the re-lock feature has been disabled; unlock-only from this version forward.
- **Fixed-port networking (payload)** — every listener pinned to exactly one port: Suite TCP/UDP 9113, PKG 13801, WebUI 17382, Suite Link punch 39295, Remote Play 9295–9297.
- **SDK v0.45** — payload rebuilt against ps5-payload-sdk v0.45 (adds kernel offsets for FW 14.00/14.10).
- **UDS trophy fix** — `evval`/`evcval` are now `long` (parsed with `strtoll`); values >INT_MAX (e.g. Ratchet's `TrophyCodeNameHash`) no longer truncate to 0.
- Security: removed hardcoded Suite Link code from UI placeholders and test defaults.
- Zero warnings, zero errors across all builds.

### ✨ New in v8.0.0

- **Remote Play runs inside PS5 Suite** — the streaming engine is `ps5chiaki.dll`, a 927 KB shim over chiaki-ng's libchiaki protocol core, embedded in the single-file `PS5Suite.exe` (self-extracts to temp). Video decodes via ffmpeg → BGRA into an Avalonia control, audio via opus → PCM → `waveOut`, input maps straight onto `ChiakiControllerState`. No second app, no Qt, no window docking — down from the 430 MB bundled engine of v7.9.0.
- **Remote Play tab** — pair, register and stream the PS5: `Pair & Connect` runs PIN → encrypted regist handshake (UDP probe + TCP 9295) → credential save → the stream opens in the suite's own window with Disconnect/Fullscreen controls. Verified live: 1080p60 + audio + keyboard input + clean disconnect.
- **Payload Remote Play commands** — `status`, `enable`/`enableuser`, `pin`/`poll`/`cancel` (libSceRemoteplay loaded dynamically — optional, never breaks firmware without it), `diag`, `setkey`, plus the regist account-id resolver.
- **Fixed: stream stability** — audio buffers could be freed while the driver was still playing them (silent crash); session context lifetime hardened.

Keyboard map: arrows = d-pad · Enter = ✕ · Esc = ◯ · F = □ · T = △ · PgUp/PgDn = L1/R1 · Q/E = L2/R2 · F1/F2 = L3/R3 · O = Options · N = Share · P = PS · Tab = touchpad.

Remote Play engine runs on **Windows x64 and Android** — the Android client decodes video via `MediaCodec` (hardware H.264) and audio via Opus, and connects over the internet through Suite Link (direct IPv6 → IPv4 → NAT punch → MQTT relay, with a transport picker to force the path). Other platforms run the full suite without it.

- Payload + client bumped together — the v8.0.0 client requires the v8.0.0 `ps5_suite_server.elf`.

### ✨ New in v7.8.1

- **On-console Web UI** — the payload now serves a full dashboard (default port 17382) with a home-screen tile (`PSUI00001`) to launch it straight from the PS5 — no PC needed. Pages: Dashboard, System with live charts, Games, Install, Trophy, Saves, Screenshots, Files, Kernel Log, Tools.
- **Web UI Saves manager** — per-save icons, friendly game names, PS4/PS5 badges, `sce_bu_*` backups hidden, and Mount / Browse / Download / Delete actions per save.
- **Web UI Lights control** — console LED colors + effects (breathe, sunrise, off/auto) and DualSense lightbar colors, from the Tools page.
- **Fixed: trophies not unlocking on FW 5.50 (issues #16/#17)** — the FW 5.50 trophy daemon rejects the `options=1` post signature with `0x80553102`. Every handle pair is now probed with `options=1` first (13.60+ stat-extraction signature) and automatically falls back to `options=0` (the 7.6.0 signature 5.50 accepts). Verified on FW 5.50: trophies unlock through the daemon's own award path.
- **Verified unlock cascade** — every trophy now goes through a 4-layer cascade (UDS post → daemon `GetTrophyInfo` query → `sceNpTrophy2SystemDebugUnlockTrophy` IPMI → direct `TRPTITLE.DAT` pin), so custom-protocol titles that reject posts still get their trophies. Response reports which layer landed each trophy.
- **`repair:<title|all>` registration repair** — suite-mounted games missing from the home screen can be re-registered in place (re-ensures `param.json`/`icon0.png` and re-runs the daemon registration). `repair:all` only touches suite mounts — real installs are never affected.
- **Fixed: payload port stacking** — the TCP listener no longer uses `SO_REUSEPORT`; redeploys could otherwise stack wedged listeners that accept but never answer (unkillable until reboot). Stale-instance takeover + 9113–9143 fallback now work as designed.
- **Fixed: client used the wrong port after fallback** — discovery found the live port but connections still dialed hardcoded 9113. The discovered port is now stored silently and used by every connection path; TCP/UDP scanning covers the full 9113–9143 range.
- **Fixed: downloads showed no progress (issue #18)** — the progress panel was only shown for uploads; single-file, folder and multi-select downloads now display percent/speed/filename and support STOP cancel.
- **Web downloads save with real filenames** — the `/dl` endpoint now sends the actual basename via `Content-Disposition`.
- Payload + client bumped together — the v7.8.1 client requires the v7.8.1 `ps5_suite_server.elf`.

### ✨ New in v7.8.0

- **Responsive mobile UI (Android)** — every page stacks its cards, toolbars and editor panes vertically on narrow screens: System Monitor, Live Sensors, Trophies, Memory Editor, PKG tools, search/sort toolbars
- Fan Control slider stretches to width; preset buttons wrap
- Memory and PKG pages scroll on phones — nothing clips off-screen
- Long game/store names ellipsize instead of clipping
- Same adaptive layout on the desktop client when the window is narrower than 700px
- **Trophy unlock: custom-event game support (payload)** — games using custom UDS extraction events (e.g. Onimusha) now unlock correctly. Events are posted under the game's own daemon-bound context (borrowed from the UDS singleton table), and a synthetic `activityEnd` triggers the daemon's extraction pass. Standard-event games are unaffected (fallback to the original fresh-context path).

### ✨ New in v7.7.0

- **Trophy unlock pipeline reworked (payload)** — NID-first symbol resolution covers NID-only exports; events now post as game-generated (`GENERATED_BY_CODEGEN`) on a fresh per-post handle; the NP callback pump (`sceNpCheckCallback`) is invoked after every post so the daemon drains the event queue; the borrowed-context path mints and binds a fresh handle via `RegisterContext` when no live game handle exists; destroying the context triggers the daemon's journal flush so the award actually lands. A new direct-award path drives `sceNpTrophy2SystemDebugUnlockTrophy`/`LockTrophy` — the daemon's own debug op on the game's registered pair.
- **Trophy timestamps now show local time** — unlock timestamps from `TRPTITLE.DAT` are stored in UTC; the client now converts them to local time before display, so you see the actual time you earned the trophy, not the UTC offset.
- **Download: per-session buffer (data corruption fix)** — the static shared 16 MB download buffer was replaced with a per-session `dl_buf` in the payload, eliminating a race condition that could corrupt file data when multiple clients download in parallel.
- **Download: 8-connection parallel folder downloads** — folder downloads now use 8 parallel connections with `SemaphoreSlim` throttling instead of sequential one-file-at-a-time.
- **Download: double-buffered I/O with ArrayPool** — file downloads overlap network reads with disk writes using two alternating buffers rented from `ArrayPool<byte>`, cutting allocation waste and improving throughput on fast LANs.
- **Removed dead `ParallelUploader` class** — unreachable upload code that always fell back to single-connection was removed.

### ✨ New in v7.6.0

- **Trophies on next-gen SDK titles** — games that never load `libSceNpTrophy2` (e.g. Onimusha: Way of the Sword and other UDS-driven releases) now work: the payload detects the title through `libSceNpUniversalDataSystem`, resolves each trophy's real unlock event from the game's own `stats_extraction.json` (custom events like `UnlockTrophy`/`trophyId`, not just the legacy `_UnlockTrophy`), and posts it through the game's UDS pipeline — the console's trophy daemon grants the award itself, with the **real system notification** and a daemon-written `TRPTITLE.DAT`.
- **Newer `TRPTITLE.DAT` layouts parsed correctly** — titles that keep hash data in the group-mask field no longer show `—/N` or get mistaken for unlocked; state rows are authoritative there, and the file editor never writes bits into non-bitmask fields.
- **Profile names in the trophy list** — sets show the real PSN username (e.g. `👤 manosmoyrtzakis`) instead of the hex user id, and the profile filter lists names too.
- **Per-save icons** — the saves list now shows each save's own `icon0.png` fetched live from the console, and the automatic `sce_bu_*` backup twins the system keeps are filtered out of the list.
- **Save mount/unmount hardening** — correct save-manager authid, atomic staged-image write-back (temp file + size check + rename), and a discard-unmount mode for read-only backups so a backup can never modify the original.
- **Post-unlock refresh** — the trophy list re-polls until the daemon flushes the new state to disk, so the count updates by itself.
- **Android packaging** — the APK ships directly as `PS5Suite.apk`, and `publish.ps1` auto-detects a Java 17+ JDK (Android Studio JBR first), so builds no longer break on machines whose `JAVA_HOME` points at an old JDK.
- **Power-sensor buffer overflow fixed** — `sceKernelGetSocPowerConsumption` fills a 0x70-byte block (8 power rails of `{mW, mV, mA}` + aux words) but was being handed a shared 4-byte slot, overflowing ~108 bytes into adjacent statics on every sensor poll. It now gets a dedicated 128-byte buffer, and **SoC power reports the sum of all 8 rails** (the total other tools show) instead of the GPU rail only. Reported by [@Marice](https://github.com/Marice) in [#13](https://github.com/manos555555/PS5-Suite/issues/13) — thank you!
- Payload + client bumped together — the v7.6.0 client requires the v7.6.0 `ps5_suite_server.elf`.

### ✨ New in v7.5.0

- **File manager context menu** — Create Folder, Change Permissions (chmod dialog: Owner/Group/Others checkboxes synced with a live octal field), Properties, Copy Path, Open Terminal Here, Download Selected (multi-select), Refresh, Sort By — plus a **permissions column** in the PS5 file list (octal mode next to each name).
- **Android app fully fixed** — the shared UI was refactored to single-view controls so the APK actually launches, and it gained a responsive mobile layout (hamburger drawer, single-pane file tabs).
- **LED/beep fix** — changing LED colours/effects no longer disables the console's beep in Settings → Beep and Light (issue #12, verified on hardware).
- Payload + client bumped together — the v7.5.0 client requires the v7.5.0 `ps5_suite_server.elf` (the directory-listing wire format gained permission bits).

---

## 🚀 Setup (2 minutes)

1. Load a jailbreak / payload loader on the PS5 — **fully compatible with both etaHEN and Kstuff** (any payload loader that accepts ELFs works). All features including mount/unmount, launch game and DualSense control work on k-stuff.
2. Open the app → expand **⚙️ Payload Settings** → **📂 Browse** → pick `payload/ps5_suite_server.elf` → **📤 Send Payload** (default port 9021).
3. The payload installs itself and starts the suite server (ports 9113–9143, first free wins — the Web UI follows at +4269, e.g. 17382 for 9113).
4. Press **🔍** to auto-discover the console on your LAN, or type its IP → **🔌 Connect**.
5. Done. Future payload updates go through **🔄 Self-Update Payload** (pushes the new ELF through the running server) or **⬆️ Update from GitHub**.

💾 Save your console(s) as named **connection profiles** for one-click reconnect.

---

## 📂 EVERYTHING the program does — feature by feature

### 🔌 Top bar / Connection

- **Auto-discover (🔍)** — broadcasts on the LAN and finds the PS5 automatically.
- **Profiles** — save multiple consoles by name, delete them, reconnect instantly.
- **Storage Info panel** — free/total space on the console's drive, **🔄 Refresh Storage** button.
- **Payload Settings** — pick the ELF, set the injection port (default 9021), **Send Payload**, **Self-Update Payload** (updates the running server in place), **Update from GitHub** (checks for new suite releases).
- **Ko-fi support link** in the header. ❤️

---

### 📁 FILE TRANSFER (main screen — dual browser)

Two side-by-side panes: your PC on the left, the PS5 on the right.

**Local PC pane**
- Browse all local drives/folders like a file manager.
- **📄 Files** — multi-select files to queue. **📁 Folder** — queue an entire folder (recursive).
- **🌐 NAS** — add a network/SMB path (`\\server\share\folder`) with **username/password login** — managed SMB client (SMBLibrary) so it works on every OS, not just Windows; credentials are kept in memory only. Guest/anonymous shares work too. Uploads stream straight from your NAS to the PS5.
- Right-click a local or NAS `.pkg` → **📦 Install on PS5** — *streams the package to the console's installer over HTTP; no 20 GB copy to the PS5 drive needed.*
- **🗑️ Clear** empties the upload queue.

**PS5 remote pane**
- Full filesystem browser: path bar + **Go**, ⭐ **favorites** (saved per console), live **search/filter** box.
- **Permissions column** — every file/folder shows its octal mode (e.g. `0755`) next to its size, read live via `stat()` on the console.
- Right-click menu: **⬇️ Download**, **⬇️ Download Selected** (multi-select), **📁 Create Folder**, **📝 Rename**, **📋 Copy**, **✂️ Move**, **� Change Permissions** (chmod dialog — R/W/X checkboxes for Owner/Group/Others synced with octal), **ℹ️ Properties** (size, type, octal+rwx mode, timestamps), **📋 Copy Path**, **💻 Open Terminal Here**, **�🗑️ Delete**, **🗑️ Delete Selected**, **🔄 Refresh**, **Sort By** — Name/Size/Date (folders stay on top).
- **📋 Copy / ✂️ Move** — opens a remote **folder picker** (browse PS5 directories, go up with "..", optionally rename the item in the same dialog). Cross-mount copies show a live progress strip (bar + `Moving X/Y MB`, payload sends updates every 32 MB); same-mount moves are instant (`rename()`).
- Downloads ask where to save on the PC; folders download recursively.

**Transfer engine**
- **📤 Upload** — parallel chunked transfers; ~**280 MB/s over Ethernet** on fast LANs (2.5GbE), saturates gigabit wired links easily. Wi-Fi will be significantly slower — use a wired connection on both PC and PS5 for full speed.
- **⬇️ Download** — folder downloads run on **8 parallel connections** and file transfers use double-buffered I/O (network read overlaps disk write) for maximum throughput.
- Duplicate files on the PS5 trigger a dialog: skip / overwrite / rename.
- Live progress bar with per-file %, overall speed (MB/s) and ETA. **⏹ STOP** cancels cleanly.
- **🎮 Mount Games** — mounts uploaded game dumps so they appear in the PS5's game list.
- **Transfer History** — every completed/failed transfer logged; **🔄 Retry** failed items, **🗑️ Remove**/**Clear All**.

---

### 🎮 GAMES (sidebar)

- Lists every installed + mounted title: name, title ID, region, version, size, path.
- **▶ Launch Game** — boots the title directly.
- **ℹ️ View Details** — full info window (param.sfo metadata, content ID, category, versions).
- **📥 Mount Game** / **🗑️ Unmount Game** — mount/unmount game images on the fly.
- **📂 Open Path** — jumps the PS5 browser straight into that game's folder.
- **📥 Mount All** batch-mounts everything in the list.

---

### 🛍️ GAMES → Homebrew Store

- Built-in **pkg-zone.com** catalog: browse/search every public PS5 homebrew package.
- Covers, version + author info, one-tap **📥 Install** — the package streams straight to the console installer.

---

### 🌐 GAMES → Prospero Store

- Built-in **homebrew.page** catalog: homebrew apps, games and tools with covers, categories and search.
- **📥 Install** — downloads the app ZIP, verifies SHA-256, extracts, uploads to /data/homebrew/ with parallel connections and auto-mounts it on the PS5 home screen.

---

### 🏆 TROPHIES

- Lists every trophy set on the console (per game) with real data parsed from TRPTITLE.DAT — names, descriptions, grades (bronze/silver/gold/platinum), icons, hidden status and earned timestamps.
- **🔓 Unlock** — awards a trophy live through the running game's own trophy pipeline (the matching title must be running — the app tells you if it isn't). Works on both legacy `libSceNpTrophy2` titles and newer UDS-driven games: each trophy's real unlock event is resolved dynamically from the game's `stats_extraction.json`, and the console's daemon performs the award — real notification, real state file. A direct-award path through the daemon's own debug op (`sceNpTrophy2SystemDebugUnlockTrophy`) is also available.
- Search/filter across sets, live refresh after every change.

> ⚠️ **Warning:** unlocking many trophies with unrealistically short/identical timestamps can look suspicious if the console ever syncs to PSN. **I take no responsibility for any ban on your account or console** — use this feature offline and at your own risk.
>
> ⚠️ **Known limitation:** a small number of games drive their trophies through a **custom protocol** rather than the standard pipelines — trophy unlocking for those titles is not supported yet and will be addressed in a future release.

---


### 💾 SAVES & MEDIA → Saves

- Enumerates all save data on the console (per user, per title) with size/type badges.
- **🔓 Mount (decrypt)** — mounts the save decrypted on the PS5 so its files are readable.
- **📂 Browse** — opens the Save Browser window into the mounted save's files.
- **🔒 Unmount & save** — unmounts and re-seals the save.
- **📥 Backup (decrypted)** — downloads the decrypted save contents to PC.
- **📥 Backup (raw image)** — downloads the raw encrypted save image 1:1.
- **📤 Restore** — pushes a backup back onto the console.
- **🗑️ Delete save** / **📂 Copy path**.

---

### 📷 SAVES & MEDIA → Screenshots

- Grid of all captured screenshots & video clips on the PS5 with **thumbnails**, filename, size, date.
- **⬇ Download** selected (or batch to a chosen folder), **🗑️ Delete**, **📋 Copy Path**.
- Double-click/preview to view the image full-size.

---

### 🖥️ SYSTEM INFO (live hardware monitor)

Everything is read **live from the console** — nothing hardcoded; values the console can't report show as unavailable.

- **System Information** — model (CFI-xxxx), serial number, architecture, OS version, CPU cores, physical RAM.
- **Extended Info** — firmware version, product code, uptime this boot, power-cycle count, BD drive presence, thermal alert status.
- **Live Sensors** (● LIVE, auto-refresh) — 🔥 CPU temp, 🌡️ SoC temp, ⚡ CPU frequency, 🔋 SoC power draw (watts).
- **CPU Usage** — per-core usage bars (all 8 cores) + total.
- **RAM** — used/free memory.
- **Network** — interface info + ⚡ **LAN speed test** (payload streams 16 MB, shows real Mbps).
- **Storage** — free/total per mount point.
- **Modules** — **🔄 Load** lists all loaded kernel/user modules.

---

### 🎯 APPS (App Manager v2)

- Lists every running app/process with real data from `sceKernelGetAppInfo`: pid, app_id, title_id, name, type, per-process **CPU%** (kernel `ki_runtime` deltas) and suspended state (`ki_stat`, no IPC stall).
- **⏸ Suspend / ▶ Resume** — SIGSTOP/SIGCONT freeze, works on any app.
- **🗑️ Kill** — ForceKillApp → KillApp → SIGKILL fallback chain.
- **🧠 Coredump** — triggers a process memory dump (needs etaHEN).

---

### 📺 REMOTE PLAY (Windows x64)

- **Fully inside PS5 Suite** — the stream engine (`ps5chiaki.dll`, a flat-C shim over chiaki-ng's libchiaki) is embedded in the exe; video, audio and input run in the app's own process and window. No external program.
- **Pair & Connect** — asks the payload for a fresh PIN + account id, runs the encrypted registration handshake from this PC (UDP probe + TCP 9295), stores credentials locally. One-time per PC.
- **Connect** — opens the suite's Remote Play window: 1080p60 stream, audio, fullscreen toggle, disconnect. Keyboard controls the console directly (map above).
- **Payload helpers** — enable service/user, live pairing status, device list, PIN issue/cancel — all driven through `remoteplay|…` payload commands.

---

### 🌍 CONNECTING OVER THE INTERNET (Suite Link)

Enter your 8-character Suite Link code and the app finds the console from anywhere — no server of your own needed. You can change the code at any time: **"Set on PS5"** writes a new code to `/data/suite_link_code.txt` (re-advertised under new MQTT topics within ~2 s, no reboot) and **"Read from PS5"** fetches the current one. Transports race automatically and the status line shows the winner:

| Transport | When it wins | Needs |
|---|---|---|
| **Direct IPv6** ✅ best | console has a global IPv6 + router allows it | firewall pinhole on the router (below) |
| **Direct IPv4** | same ISP routable address | port-forward (rare on CGNAT) |
| **NAT punch** | both sides UDP, non-symmetric NAT | nothing — automatic |
| **MQTT relay** | always works | nothing — slowest, last resort |

**To get the fastest path (direct IPv6), open the ports on your router once:**

1. **Find the console's IPv6** — on the PS5: *Settings → Network → View Connection Status → IPv6 Address* (the long one starting with `2…`). It must be a **global** address, not `fe80::`.
2. **Log into your router** — usually `http://192.168.0.1` or `http://192.168.1.1` (check the sticker on the modem for the admin page + password).
3. **Find the IPv6 firewall/pinhole page** — named differently per brand:
   - **FRITZ!Box** → *Internet → Permit Access → IPv6* → add the console, tick the ports below
   - **TP-Link / Netgear / Asus** → *Advanced → IPv6 → Firewall* (or *Security → IPv6 Firewall*) → add an *inbound/allow* rule
   - **ISP routers (Vodafone, Cosmote, Nova…)** → *Internet → Port Forwarding / Firewall → IPv6* → *Add rule / Pinhole*
4. **Add a rule allowing inbound traffic to the PS5's IPv6** on these ports:

   | Port | Protocol | Used for |
   |---|---|---|
   | `9113` | TCP + UDP | Suite Link (management, files, trophies, discovery — everything) |
   | `9295` | TCP + UDP | Remote Play registration/control |
   | `9296`–`9297` | UDP | Remote Play video/audio/input stream |

   Source: *any* (or `::/0`) · Destination: **the PS5's IPv6 address** · Action: **allow/permit**.
5. **Save + reconnect.** In the app set the transport picker to **Auto** (or **Direct IPv6** to force it) and connect — the status line should show `direct ipv6`.

> 💡 No IPv6 or locked-down router? Just connect anyway — NAT punch and the MQTT relay need **zero** router config; they're slower but always work. If the pinhole is missing, Auto falls back by itself.
>
> 💡 **Dynamic IPv6**: some ISPs rotate the prefix periodically. If direct IPv6 stops working, check the console's current address (*Settings → Network → View Connection Status*) and update the firewall rule. With FRITZ!Box and some others you can allow the device by name instead of a fixed address — the rule survives prefix changes.

---

### ⚡ POWER & DEVICES

- **Power** — reboot / shutdown / rest mode from the app.
- **USB drives** — list mounted USB storage.
- **🎮 Controller (DualSense)** — live pad state + controller info read through a remote bridge into `SceShellUI` (the payload gets no pad session of its own, so it resolves the real logged-in user remotely — nothing hardcoded). **Lightbar**: set any RGB color.
- **💡 Console LED** — real `/dev/icc_indicator` interface with three hardware channels (blue `0x01` / white `0x11` / amber `0x21` — verified on hardware): pick a color, run effects (breathe, sunrise, blink, chase…), `auto` hands control back to the system.
- **🔔 Notify** — push a custom notification to the PS5 screen.
- **🔊 Beeper** — console beep / mute.

---

### 🔧 TOOLS → Shell

- Run shell commands directly on the PS5 — type, Enter, output streams back.
- Scrollable session history, **🗑️ Clear**, **💾 Save Log** to a PC file.

---

### 🔧 TOOLS → Search

- **🔄 Start Index** — the payload crawls the PS5 filesystem and builds a searchable index.
- Instant search across **every** file/folder on the console — results open straight in the browser.
- **🔃 Refresh** rebuilds, **🗑️ Clear** wipes the index.

---

### 🔧 TOOLS → Fan Control

- Reads the current fan threshold + live temperature.
- **🌡️ Set Threshold** — custom temperature target.
- Presets: **❄️ Cool (50 °C)**, **⚖️ Balanced (60 °C)**, **🔇 Quiet (70 °C)**.

---

### 🔧 TOOLS → Memory

- **Process list** of running PS5 processes (**↻ refresh**).
- **🔍 First Scan / 🔎 Next Scan** — cheat-style value scanning in a process's memory.
- **📖 Read** (hex dump, 256 B) and **✏️ Write** at arbitrary addresses — full debugging/patching toolkit.

---

### 🔧 TOOLS → Kernel Log

- Live kernel log viewer — choose **Last 32 KB / Last 128 KB / Full buffer**.
- **Auto (3s)** checkbox for continuous refresh, **🔄 Refresh** manual.
- **📋 Copy** to clipboard, **💾 Save…** to file.

---

### 🔧 TOOLS → PKG

- **Install PKG** — three ways:
  - **📋 Browse PS5** — pick a `.pkg` already on the console.
  - **💻 Browse PC** — pick a local `.pkg`; it's **streamed over HTTP** (ranged requests, persistent keep-alive connections, 512 KB relay) straight into the PS5 installer. Verified with a 22.7 GB package.
  - Via the local-pane right-click "Install on PS5".
- **🔄 Check Status** — queries the console's installer for progress/result.
- Staged `.pkg` files on the PS5 are **auto-deleted** after successful install.
- PS5 notifications show the real package name (not a generic label).

- **Convert to FPKG** — turn an extracted game folder into an installable fake package **entirely on the PC**:
  - Point at the source folder (with `sce_sys`), choose output, done.
  - Auto-fills title ID / content ID / version from `param.json`.
  - Optional **fake-signing** of `eboot.bin`, `*.elf`, `*.prx`, `*.sprx` (originals restored automatically after the build).
  - Builds the full PS5 container: inner PFS image, encrypted+signed outer PFS, CNT entry table, all SHA3-256 digests, RSA-3072 signatures, FIH finalization — powered by the bundled **LibProsperoPkg** engine.
  - Output verified against real package structure (field-accurate, validator-tested).

---

### 🧾 DEBUG LOG (sidebar)

- Timestamped log of **every** operation the app performs — connections, transfers, errors, PS5 responses.
- **📋 Copy Log**, **🗑️ Clear Log**; a `ps5suite_*.log` file is also written automatically next to the executable for post-mortem debugging.
- **Failed transfers** section with per-item **🔄 Retry** and **🗑️ Remove**.

---

### 🌐 WEB UI (on-console, no PC needed)

- The payload serves a full dashboard over HTTP — open `http://<ps5-ip>:17382` in any browser, or launch it straight from the **PSUI00001 home-screen tile** the payload registers.
- **Dashboard** — storage, mounted games, running apps and payload status at a glance.
- **System** — live CPU/memory/network charts and hardware info in real time.
- **Games / Install** — browse mounted titles, launch, mount, install PKGs.
- **Trophy** — trophy sets with icons, per-trophy state, unlock, registration repair.
- **Saves** — save manager with per-save icons, game names, PS4/PS5 badges (`sce_bu_*` backups hidden): Mount, Browse, Download (real filenames), Delete.
- **Screenshots / Files / Kernel Log** — media preview + download, full filesystem browser, live klog.
- **Tools** — notifications, shell, fan control, network + controller info, **Lights** (console LED colors/effects + DualSense lightbar), power actions.

---

## 🔧 Technical details

| Component | Technology |
|---|---|
| Client (Windows/Linux/macOS/Android) | C# / .NET 10, **Avalonia UI** — one shared codebase everywhere |
| On-console payload | C, built with the **PS5 Payload SDK** (`prospero-clang`) |
| FPKG engine | **LibProsperoPkg** (bundled) — CNT/PFS/PFSC/PFSv3/FIH build, read & verify |
| Transfer protocol | Raw TCP, chunked + parallel streams |
| Suite server ports | 9113–9118 (scans until it finds the live server) |
| Local PKG streamer ports | 18990–19009 |
| Payload proxy port | 13801+ |

- All heavy work (transfers, FPKG builds) runs on background threads — the UI never freezes.
- **The Android app is the same Avalonia codebase** — shared protocol layer, same dark theme and the same feature set as the desktop build; on narrow screens it switches to a hamburger-drawer layout with single-pane file tabs. Source is in `client-avalonia-android/`; a signed `PS5Suite.apk` ships in every release.

---

## 📱 Android app

- Same Avalonia UI/protocol as desktop — every feature above works on Android: transfers, games (mount/launch/stop), both stores, trophies, saves, system info, tools, devices, debug log.
- **Responsive mobile layout** — on phone-width screens the sidebar collapses into a **☰ drawer** (tap outside or a menu item to close), the Local/PS5 file panes become a tab switcher, and the storage info bar stacks vertically. On desktop-sized windows the full layout stays.
- Signed APK, version synced with desktop releases. Install → enter PS5 IP → Connect. Same payload, same ports.

---

## 🐞 Found a bug?

Please report it in the **[Issues](https://github.com/manos555555/PS5-Suite/issues)** section — include your firmware, what you were doing, and any error message or log output (the app's Debug Log tab + the `ps5suite_*.log` file next to the executable help a lot). Every report gets looked at — the last two fixes came straight from user reports. 🙏

---

## ⚠️ Disclaimer

Requires a jailbroken PS5 with a payload loader. Save backup/restore and memory write features modify console state — **use at your own risk**. For personal/educational use; no copyrighted content included.

**🏆 Trophy features:** unlocking trophies — especially many at once or with unrealistic timestamps — may look suspicious to Sony if your console or account ever syncs with PSN. **The author takes no responsibility for any ban of your account or console.** Use offline, at your own risk.

**Enjoy. — Manos**
