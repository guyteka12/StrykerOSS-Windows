# StrykerOSS — Windows

> Suite de test d'intrusion de bureau **gratuite et open-source pour Windows**. Tests autorisés uniquement.
> Portage Windows de [zalexdev/strykerapp](https://github.com/zalexdev/strykerapp) (Android) — même architecture et mêmes fonctionnalités, fonctionnement réel uniquement, **GPL-3.0**.

**StrykerOSS Windows** réimplémente la suite mobile StrykerOSS en **véritable logiciel Windows natif** (WPF, .NET 8, fichier unique auto-suffisant). Là où l'app Android utilisait le **root du téléphone + un chroot Debian**, la version Windows utilise les **droits Administrateur Windows (UAC)** et les **API natives Windows** — **aucun matériel externe requis** (Wi-Fi et Bluetooth intégrés à la machine).

**L'interface est intégralement en français.**

---

## ⚠️ L'éthique d'abord — Tests autorisés uniquement

StrykerOSS est fourni à des fins **ÉDUCATIVES uniquement**.

- Ne scannez, ne testez et n'auditez que des systèmes **qui vous appartiennent** ou pour lesquels vous disposez d'une **autorisation écrite explicite**.
- L'accès non autorisé à des systèmes informatiques est **illégal** dans la plupart des juridictions.
- L'application impose un accord de consentement au démarrage et consigne toute l'activité localement.
- L'éthique avant tout. Obtenez toujours la permission. Respectez les systèmes et les données.

---

## Installation

| Paquet | Description |
|---|---|
| `StrykerOSS-Setup-1.1.0.exe` | Installeur (Inno Setup, en français) : raccourcis menu Démarrer + bureau, désinstalleur, page de licence GPL. |
| `StrykerOSS-portable.exe` | Version portable en fichier unique (.NET 8 auto-suffisant, aucun runtime à installer). |

L'application **exige les privilèges Administrateur** (invite UAC Windows) — c'est l'équivalent Windows du root Android : le contrôle Wi-Fi natif, les changements d'adresse MAC, les redémarrages d'adaptateurs et l'ARP brut exigent l'élévation.

Les deux artefacts sont produits automatiquement par **GitHub Actions** à chaque poussée (voir `.github/workflows/build-windows.yml`) et joints aux artefacts / releases du run.

---

## Première configuration (assistant d'installation)

Au **premier lancement**, après l'accord de consentement, l'**assistant de première configuration** s'ouvre automatiquement. Il installe **réellement** (aucune simulation) tout ce dont la plateforme a besoin — l'installation peut être lourde, tout est automatisé :

| Composant | Méthode principale | Méthode de secours intégrée | Taille |
|---|---|---|---|
| **Nmap + Npcap** | `winget install Insecure.Nmap` | Installeur officiel `nmap.org` en silencieux (`/S`, Npcap inclus) | ≈ 40 Mo |
| **Nuclei** | Archive officielle projectdiscovery (GitHub) | — (téléchargement direct toujours possible) | ≈ 60 Mo |
| **Hashcat** | `winget install hashcat.hashcat` | `choco install hashcat` si Chocolatey présent, sinon mode d'emploi | ≈ 100 Mo |
| **Wireshark / tshark** | `winget install Wireshark.Wireshark` | Installeur officiel `wireshark.org` silencieux | ≈ 90 Mo |
| **Metasploit** (optionnel) | MSI officiel Rapid7 via `msiexec /qn` | `winget install Metasploit.Framework` | ≈ 1 Go |
| **Index Exploit-DB** | CSV officiel GitLab (recherche hors ligne ensuite) | — | ≈ 20 Mo |

L'assistant est **rappelable à tout moment** : onglet **Gestionnaire d'outils → « Relancer l'assistant d'installation »**.

### ❓ Que faire si l'installation automatique échoue (méthodes de secours manuelles)

1. **winget introuvable** (rare) : installez « **Programmes d'installation d'applications** » depuis le Microsoft Store, puis relancez l'assistant. L'assistant bascule alors automatiquement sur les méthodes de secours (installeurs officiels silencieux).
2. **Nmap manuel** : téléchargez https://nmap.org/dist/nmap-7.95-setup.exe → installation normale (laissez cocher Npcap) → relancez StrykerOSS, la détection est automatique (`C:\Program Files\Nmap\nmap.exe`).
3. **Nuclei manuel** : https://github.com/projectdiscovery/nuclei/releases → `nuclei_3.3.9_windows_x64.zip` → décompressez `nuclei.exe` dans le dossier outils (`%LOCALAPPDATA%\StrykerOSS\tools`) ou renseignez le chemin dans **Paramètres**.
4. **Hashcat manuel** : https://hashcat.net/hashcat/ → décompressez `hashcat.exe` dans le dossier outils.
5. **Metasploit manuel** : https://windows.metasploit.com/metasploitframework-latest.msi (double-clic = installation normale) → puis dans **Paramètres**, pointez le chemin vers `msfconsole.bat`. Alternative : Kali Linux dans **WSL** (`wsl --install`) puis `sudo apt install metasploit-framework`.
6. **Wireshark manuel** : https://www.wireshark.org/download.html (l'option « Tshark » en ligne de commande est utile aux modules d'analyse).
7. **Sans Internet** : tous les modules qui utilisent les API natives Windows (Wi-Fi, réseau local, ports, MAC, USB, HID, sessions, rapports, journaux) fonctionnent **sans aucun outil externe**. Les outils externes ne sont nécessaires que pour Nmap/Web/Metasploit/Hashcat/Exploit-DB.
8. **Le service Wi-Fi refuse** (erreur Win32) : démarrez le service « **Configuration automatique WLAN** » (`services.msc` → WlanSvc → Démarrer, type : Automatique).

---

## Architecture (identique à l'app Android)

```
src/StrykerOSS/
├── App.xaml(.cs)          Point d'entrée DI + consentement + assistant 1ère config + filets de sécurité
├── Pages.cs               Registre des pages (sûr : une page en erreur n'arrête jamais l'app)
├── Core/
│   ├── Contracts.cs       Tous les contrats/records (équivalent des contrats Stryker mobile)
│   └── Services/          Services RÉELS : wlanapi natif, SQLite, registre, SendInput, MSGRPC, QuestPDF...
├── ViewModels/            MVVM (CommunityToolkit) — un VM par module
├── Views/                 21 modules + assistant de configuration
└── Themes/Dark.xaml       Thème sombre unique (jetons de design)
```

Modules : Tableau de bord, Réseaux Wi-Fi (scan/connexion/profils via **Native Wifi API**), Réseau local (ICMP+ARP+DNS réels), Scanner Nmap, Scanner web (Nuclei), Base d'exploits (hors ligne), Arsenal (modèles de commandes), Handshakes (analyse EAPOL pcap + export hashcat 22000), WhisperPair (BLE réel), Changeur d'adresse MAC (registre, réel), Attaques HID (DuckyScript + SendInput réel), Arsenal USB, Metasploit (MSGRPC réel + msfvenom), Sessions, Console (PowerShell/cmd élevé), GeoMac, Projets, Rapports (PDF/HTML/Markdown), Gestionnaire d'outils, Journaux, Paramètres.

---

## Compilation

```bash
dotnet publish src/StrykerOSS/StrykerOSS.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

L'installeur se construit avec Inno Setup : `ISCC.exe installer/StrykerOSS.iss /DAppVersion=1.1.0`.

## Licence

GPL-3.0 — comme le projet d'origine. Usage éducatif uniquement.
