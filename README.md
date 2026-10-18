# 🎮 PS5 Suite v7.5.0 — Complete PS5 Management Platform

**By Manos**

All-in-one management suite for jailbroken PS5 consoles. One app — on **Windows, Linux, macOS and Android** — that does everything: high-speed file transfers, game mount/launch, **trophy unlock & re-lock**, decrypted save backup/restore, screenshots, live hardware monitoring, PKG streaming installs, full FPKG building on the PC, an integrated Homebrew Store + Prospero Store, memory tools, fan control, kernel log, a remote shell — plus console LED control and DualSense lightbar support.

<div align="center">

### ☕ Enjoying PS5 Suite?

**This project is built for the PS5 scene, for free — if it saved you time or you just like what we do, consider buying us a coffee. Every cup goes straight back into new features, fixes and payload wizardry.** 🍻

**Got questions, ideas or found a bug? Join the community on Discord — we hang out there!**

[![Ko-fi](https://img.shields.io/badge/Ko--fi-FF5E5B?style=for-the-badge&logo=ko-fi&logoColor=white)](https://ko-fi.com/manosmourtzakis)
[![Follow on X](https://img.shields.io/badge/Follow%20on%20X-000000?style=for-the-badge&logo=x&logoColor=white)](https://x.com/manos_moyrtzis)
[![Join our Discord](https://img.shields.io/badge/Join%20our%20Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/eRXG2Csmpy)

</div>

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

### ✨ New in v7.5.0

- **File manager context menu** — Create Folder, Change Permissions (chmod dialog: Owner/Group/Others checkboxes synced with a live octal field), Properties, Copy Path, Open Terminal Here, Download Selected (multi-select), Refresh, Sort By — plus a **permissions column** in the PS5 file list (octal mode next to each name).
- **Android app fully fixed** — the shared UI was refactored to single-view controls so the APK actually launches, and it gained a responsive mobile layout (hamburger drawer, single-pane file tabs).
- **LED/beep fix** — changing LED colours/effects no longer disables the console's beep in Settings → Beep and Light (issue #12, verified on hardware).
- Payload + client bumped together — the v7.5.0 client requires the v7.5.0 `ps5_suite_server.elf` (the directory-listing wire format gained permission bits).

---

## 🚀 Setup (2 minutes)

1. Load a jailbreak / payload loader on the PS5 — **fully compatible with both etaHEN and Kstuff** (any payload loader that accepts ELFs works). All features including mount/unmount, launch game and DualSense control work on k-stuff.
2. Open the app → expand **⚙️ Payload Settings** → **📂 Browse** → pick `payload/ps5_suite_server.elf` → **📤 Send Payload** (default port 9021).
3. The payload installs itself and starts the suite server (ports 9113–9116).
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
- Downloads ask where to save on the PC; folders download recursively.

**Transfer engine**
- **📤 Upload** — parallel chunked transfers; ~**280 MB/s over Ethernet** on fast LANs (2.5GbE), saturates gigabit wired links easily. Wi-Fi will be significantly slower — use a wired connection on both PC and PS5 for full speed.
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
- **🔓 Unlock** — awards a trophy live through the running game (the matching title must be running — the app tells you if it isn't).
- **🔒 Lock** — re-locks a trophy by rewriting the set's unlock bitmasks in place (group masks, row flags and timestamps cleared) — as if it was never earned.
- Search/filter across sets, live refresh after every change.

> ⚠️ **Warning:** unlocking many trophies with unrealistically short/identical timestamps can look suspicious if the console ever syncs to PSN. **I take no responsibility for any ban on your account or console** — use this feature offline and at your own risk.

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
