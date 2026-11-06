# StrykerOSS — Windows

> A free and open-source **desktop pentest suite for Windows**. Authorized testing only.
> Windows port of [zalexdev/strykerapp](https://github.com/zalexdev/strykerapp) (Android) — same architecture and feature set, real functionality only, **GPL-3.0**.

**StrykerOSS Windows** reimplémente la suite StrykerOSS mobile en **véritable logiciel Windows natif** (WPF, .NET 8, fichier unique auto-suffisant). Là où l'app Android utilisait le **root du téléphone + un chroot Debian**, la version Windows utilise les **droits Administrateur Windows (UAC)** et les **API natives Windows** — **aucun matériel externe requis** (Wi-Fi et Bluetooth intégrés).

---

## ⚠️ Ethics first — Authorized testing only

StrykerOSS is provided for **EDUCATIONAL purposes only**.

- Only scan, test or audit systems **you own** or have **explicit written permission** to test.
- Unauthorized access to computer systems is **illegal** in most jurisdictions.
- The app enforces an on-start consent agreement and logs all activity locally.
- Ethics > Everything. Always get permission. Respect systems and data.

---

## Install

| Package | Description |
|---|---|
| `StrykerOSS-Setup-1.0.0.exe` | Installer (Inno Setup): Start-menu + desktop shortcuts, uninstaller, GPL license page. |
| `StrykerOSS-portable.exe` | Portable single-file build (~self-contained .NET 8, no runtime install needed). |

The app **requires Administrator privileges** (Windows UAC prompt) — this is the Windows equivalent of Android root: Native Wi-Fi control, MAC address changes, adapter restarts and raw ARP all need elevation.

Both artifacts are produced automatically by **GitHub Actions** on every push (see `.github/workflows/build-windows.yml`) and attached to the run's artifacts / releases.

## Feature mapping (Android → Windows)

| Android (strykerapp) | Windows (this project) | Real implementation |
|---|---|---|
| Root / su | **UAC Administrator** (app manifest `requireAdministrator`) | `WindowsPrincipal` elevation check |
| Wi-Fi scan (root or USB monitor adapter) | **Wi-Fi Networks** — scan, connect, profiles | **Native Wifi API** (`wlanapi.dll`) on the **built-in adapter — no external hardware** |
| Handshake capture (monitor mode) | **Handshakes** — real `.pcap/.cap/.pcapng` EAPOL forensic parser + **hashcat 22000 export** | Hand-rolled pcap/pcapng + 802.11 + EAPOL parser (monitor-mode *capture* physically impossible on Windows stock stack — analysis of lab captures is real) |
| MAC changer | **MAC Changer** | Real registry `NetworkAddress` override + adapter restart (`netsh`), random locally-administered MAC, profiles in DB |
| Local network (Nmap discovery) | **Local Network** | Real ICMP ping sweep + `SendARP` + DNS + **IEEE OUI vendor lookup** (bundled, 40k+ real entries) |
| Nmap | **Nmap Scanner** | Real `nmap.exe` wrapper: profiles, live output, XML parsing, HTML export |
| Web scanner (Nuclei) | **Web Scanner** | Real `nuclei` binary (auto-download pinned release), JSON findings, severity grouping, CSV export |
| Arsenal (templates + args) | **Arsenal** | Real command templates with `{IP} {PORT} {MAC} {GW} {MASK}` substitution + real execution |
| Exploit DB (SearchSploit) | **Exploit Database** | Real offline search over the official Exploit-DB CSV + real file fetch |
| WhisperPair (BLE / Fast Pair) | **WhisperPair (BLE)** | Real `BluetoothLEAdvertisementWatcher` (built-in BT), Fast Pair model decode, real GATT enumeration |
| HID attacks (USB gadget DuckyScript) | **HID Attacks** | Real DuckyScript parser + real `SendInput` execution **on this machine** (layout-aware via `VkKeyScanW`) — the Windows equivalent of a USB-Rubber-Ducky is not possible without gadget hardware |
| USB Arsenal (gadget profiles) | **USB Arsenal** | Real USB/PnP enumeration, real ISO mount (`Mount-DiskImage`), real eject |
| Metasploit (chroot console) | **Metasploit** | Real detection + **MSGRPC client** (MessagePack), sessions, module search, console run, `msfvenom` payload generation |
| GeoMac (OSM of BSSIDs) | **GeoMac** | Real ip-api geolocation, real WiGLE CSV import, KML/CSV export, OSM links |
| Terminal (chroot) | **Console** | Real elevated PowerShell / cmd / WSL process |
| Core manager (chroot mount/repair) | **Tools Manager** | Real install/verify for Nmap, Nuclei, Metasploit, Hashcat (`winget` when available) |
| Sessions / Reports / Projects | **Sessions / Reports / Projects** | Real SQLite database (WAL), real PDF (QuestPDF) / HTML / Markdown reports, project backup ZIP |
| — | **Dashboard / Logs / Settings** | Real local stats, live log file + viewer, settings persisted as JSON |

Everything is **real**: real sockets, real APIs, real processes, real files. No simulation, no fake data.

## Optional external tools

| Tool | Needed for | Install |
|---|---|---|
| [Nmap](https://nmap.org/download) | advanced network scanning | `winget install Insecure.Nmap` or Tools Manager |
| Nuclei | web scanning | **auto-downloaded** (pinned official release) from Tools Manager / Web Scanner |
| [Metasploit](https://www.metasploit.com/download) | exploit sessions / payloads | manual install or WSL; then start `msfrpcd -P <pass> -S false` |
| [Hashcat](https://hashcat.net/hashcat/) | cracking exported 22000 hashes | `winget install hashcat.hashcat` |

Everything else works out of the box with **zero external hardware and zero dependencies**.

## Data & privacy

All data stays on your machine: `%LOCALAPPDATA%\StrykerOSS\` (SQLite DB, reports, scans, captures, logs, settings). No telemetry, no accounts, no tracking.

## Build from source

```powershell
dotnet publish src/StrykerOSS/StrykerOSS.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Requirements: Windows 10+ (x64), .NET 8 SDK. CI builds on `windows-latest` with Inno Setup producing the installer.

## Architecture

```
src/StrykerOSS/
├── App.xaml(.cs)            # DI composition root, consent gate
├── Pages.cs                 # navigation registry (21 modules)
├── Themes/Dark.xaml         # design tokens (single source of truth)
├── Core/Contracts.cs        # models + service interfaces (architecture map)
├── Core/Services/           # 17 real implementations
├── ViewModels/              # CommunityToolkit.Mvvm
└── Views/                   # WPF views (dark theme)
```

Service-per-module architecture mirrors the Android app's module structure. Swapping any backend keeps the UI intact.

## Credits & license

- Original project & idea: **[zalexdev/strykerapp](https://github.com/zalexdev/strykerapp)** by Anastasis Vasileiadis — *Learn. Secure. Defend.*
- This Windows port: GPL-3.0 (same license as the original, see `LICENSE`).
- Bundled data: IEEE OUI database (`standards-oui.ieee.org`) — free for use.
- External tools (optional, downloaded separately): Nmap (NPSL), Nuclei (MIT), Metasploit (BSD-3), Hashcat (GPL) — keep their own licenses.

> **KNOWLEDGE. ETHICS. IMPACT.** Cybersecurity is not just a skill, it's a responsibility.
