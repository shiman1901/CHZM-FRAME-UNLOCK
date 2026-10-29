# CHZM FRAME UNLOCK

 Frame Generation launcher and mod manager for NVIDIA RTX 20 / 30 (`SM86`) GPUs.
 Windows 10/11 x64 | .NET 8 WPF | [Download Latest Release](https://github.com/shiman1901/CHZM-FRAME-UNLOCK/releases)

---

<img width="1280" height="818" alt="image" src="https://github.com/user-attachments/assets/97bbb7cf-77e4-4765-9f8f-f4cb961d204e" />

---

## EN // OVERVIEW

**CHZM FRAME UNLOCK** combines four Frame Generation methods for NVIDIA RTX 20/30 series cards into a single interface — from internal proxy DLL injection to external screen capture (`LIVE FLOW`) for anti-cheat protected games.

### Modes

| Mode | Technology | Multiplier | Use Case |
| :--- | :--- | :---: | :--- |
| **`CLASSIC`** | `dlssg_for_sm86` + `M-INT8` / `FG-Lite` | Up to 6X | Games with a native DLSS Frame Generation slider |
| **`ENCORE`** | `RTX Encore v1.4.5` + In-game menu (`F8`) | Up to 6X | Dynamic FG (display Hz target), unified SR/RR/FG pipeline |
| **`SMOOTH`** | Driver-level `NVIDIA Smooth Motion` (`NvPresent64.dll`) | 2X | DX11 / DX12 / Vulkan games without native DLSS |
| **`LIVE FLOW`** | External `DXGI` + `Optical Flow` + `DLSS MFG` | 2X | Online games with Anti-Cheat (does not touch game files) |

### Features
* Automatic game detection for Steam, Epic Games, GOG, and local folders.
* Automatic anti-cheat detection (locks internal proxy modes and switches to `LIVE FLOW`).
* `LIVE FLOW` memory pre-warm, instant toggle via `F9`, `Nitro [N]` hold mode, auto-pause on `Alt+Tab`, and RTSS profile filtering.

### Quick Start
1. Download `CHZM FRAME UNLOCK Setup.exe` or `portable.zip` from [**Releases**](https://github.com/shiman1901/CHZM-FRAME-UNLOCK/releases).
2. Run `CHZM FRAME UNLOCK.exe` as Administrator.
3. Select a game, pick a mode, and launch.

### Build from Source
Requires .NET 8.0 SDK. Third-party binaries (`DLSS\` and `ДОПОЛНЕНИЕ\`) are not included in Git — see [`docs/PACKAGES.md`](docs/PACKAGES.md).
```powershell
dotnet build CHZMFRAMEUNLOCK1.csproj -c Release
dotnet publish CHZMFRAMEUNLOCK1.csproj -c Release
