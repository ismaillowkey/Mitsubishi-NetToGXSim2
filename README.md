# NetToGXSim2

**Mitsubishi GX Works 2 Simulator Network Protocol Bridge**  
*Version 0.6.0 | Developed by Ismail Lowkey*

[![Platform](https://img.shields.io/badge/Platform-Windows%20x86%20%7C%20x64-blue.svg)]()
[![Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2-purple.svg)]()
[![Target](https://img.shields.io/badge/Target-MELSOFT%20GX%20Simulator%202-red.svg)]()
[![Protocol](https://img.shields.io/badge/Protocol-MC%20Protocol%20(3E%20%26%201E%20TCP%2FUDP)-green.svg)]()
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)]()

---

## 📥 Download Latest Installer

Download the latest installer package from the [GitHub Releases](https://github.com/ismaillowkey/Mitsubishi-NetToGXSim2/releases/latest) page:

| File | Platform | Download Link |
| :--- | :--- | :--- |
| **NetToGXSim2 (Setup Installer)** | Windows 7 / 8 / 10 / 11 (32-bit / 64-bit) | [Download Setup (.exe)](https://github.com/ismaillowkey/Mitsubishi-NetToGXSim2/releases/latest) |

---

## 📖 Overview

**NetToGXSim2** is a lightweight, high-performance bridge application that connects **Mitsubishi GX Simulator 2** (the simulation engine bundled with **GX Works 2**) directly to external Ethernet networks via the standard **MELSEC Communication (MC) Protocol (TCP & UDP)**.

Traditionally, Mitsubishi's GX Simulator 2 runs only as an isolated local COM process (`SimManager.exe`), making it impossible for external HMIs, SCADA packages, or IoT gateways to communicate with the simulated PLC without physical hardware. **NetToGXSim2** removes this limitation by communicating directly with the simulator engine (**Zero-Configuration**, no MX Component utility setup needed) and hosting dual Ethernet TCP/UDP servers that translate MC Protocol requests into simulator memory reads/writes with sub-millisecond response times.

---

## 🏛 Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Mitsubishi GX Works 2                    │
│                      (Ladder Program)                       │
└──────────────────────────────┬──────────────────────────────┘
                               │ Virtual COM Bus
┌──────────────────────────────▼──────────────────────────────┐
│                    GX Simulator 2 Engine                    │
│             (SimManager.exe / Virtual FX CPU)               │
└──────────────────────────────┬──────────────────────────────┘
                               │ Direct COM Engine Interface (ActProgType / ActUtlType)
┌──────────────────────────────▼──────────────────────────────┐
│                  NetToGXSim2                                │
│  ┌─────────────────────────┐     ┌────────────────────────┐ │
│  │   MC TCP/UDP Server 1   │     │   MC TCP/UDP Server 2  │ │
│  │   (Default: Port 5001)  │     │   (Default: Port 6000) │ │
│  └────────────┬────────────┘     └───────────┬────────────┘ │
└───────────────┼──────────────────────────────┼──────────────┘
                │ Ethernet TCP / UDP (MC Protocol)
    ┌───────────┴──────────┬───────────────────┴───────────┐
    │                      │                               │
┌───▼─────────────┐ ┌──────▼───────────────┐ ┌─────────────▼───┐
│  Weintek HMI    │ │    SCADA / Node-RED   │ │  Python / C# /  │
│ (EasyBuilder)   │ │  (Ignition, VTScada) │ │ OPC-UA Server   │
└─────────────────┘ └──────────────────────┘ └─────────────────┘
```

---

## ✨ Features

- **Direct Simulator Connection (Zero-Configuration)**:
  - Connects directly to the GX Simulator 2 engine via `ActProgType` without requiring manual Logical Station configuration in MX Component Utility.
  - Automatic probing and detection for FX Series (`FX3U`, `FX3G`, `FX3S`, `FX2N`, `FX1N`, `FX0N`) and Q Series (`Q00J`, `Q00`, `Q01`, `Q02`).
  - Automatic fallback to `ActUtlType` station scanning if needed.

- **Dual Simultaneous TCP & UDP MC Servers**:
  - **Server 1**: Default port 5000 (auto-increments to 5001 if port 5000 is occupied). Started automatically at launch.
  - **Server 2**: Default port 6000. Stopped by default for secondary clients or testing.
  - Simultaneous TCP and UDP listeners on the exact same port to accommodate any HMI or SCADA protocol setting.

- **Intelligent Port Conflict Resolution**:
  - Automatically checks TCP and UDP port availability during startup and manual start.
  - If a port is occupied by Windows services (e.g., `svchost.exe` / IP Helper on port 5000) or other software, NetToGXSim2 automatically steps up by 1 (`port++`) until an open port is found.

- **High-Speed PlcMemoryMirror Architecture**:
  - In-memory cache mirror with dedicated STA worker thread, delivering sub-millisecond read times with zero GUI freezing.

- **Comprehensive MC Protocol Support**:
  - **QnA 3E Binary & ASCII**:
    - `0x0101`: CPU Model Type Read (returns standard `Q02UCPU` / `FX3U` identification for instant HMI handshakes).
    - `0x0401`: Batch Read (Bit & Word units).
    - `0x0403`: Random Read (Word & DWord multi-address reading).
    - `0x1401`: Batch Write (Bit & Word units).
    - `0x1402`: Random Write (Scattered Bit & Word/DWord writes).
  - **A-1E Binary & ASCII** (Legacy A Series & FX Series):
    - Subcommand `0x00`: Read Bit devices.
    - Subcommand `0x01`: Read Word devices.
    - Subcommand `0x02`: Write Bit devices.
    - Subcommand `0x03`: Write Word devices.

- **Interactive Hardware Simulation Panel**:
  - **Inputs Rack (X0 - X7)**: 8 interactive toggle switches with blue LED status indicators to force PLC input states.
  - **Outputs Rack (Y0 - Y7)**: 8 glowing green LED indicator lamps reflecting virtual PLC coil logic in real-time.

- **Background Auto-Update Checker**:
  - Silently queries GitHub releases on startup and displays non-intrusive update status indicators.

- **GX Simulator 2 Process Manager**:
  - Built-in **"Exit GX Simulator 2"** feature to cleanly terminate background simulator processes (`SimManager.exe` and `IOSystem.exe`).

---

## 📋 System Requirements

| Requirement | Specification |
| :--- | :--- |
| **Operating System** | Windows 7 / 8 / 10 / 11 (32-bit or 64-bit) |
| **Runtime** | .NET Framework 4.7.2 or higher |
| **PLC Software** | Mitsubishi **GX Works 2** (with GX Simulator 2 installed) |
| **COM Components** | MELSOFT Communication Library (`ActProgType` / `ActUtlType`) |

> [!IMPORTANT]
> **GX Simulator 2 vs GX Simulator 3:**  
> This software is specifically engineered for **GX Simulator 2** (bundled with **GX Works 2**).

---

## 🚀 Quick Start Guide

### Step 1: Start Simulation in GX Works 2
1. Open your project ladder logic in **GX Works 2**.
2. Start the simulation: navigate to **Debug** → **Start/Stop Simulation**.
3. Ensure the simulation window (`SimManager.exe`) appears and GX Works 2 is in **Monitor Mode**.

> [!WARNING]
> **Do NOT alter Transfer Setup in GX Works 2!**  
> In GX Works 2 under **Connection Destination → Transfer Setup**, keep the connection set to **GX Simulator 2** (default).

### Step 2: Launch NetToGXSim2
1. Run `NetToGXSim2.Wpf.exe` (or install via the setup installer).
2. Verify that the status header badge shows **`GX Sim 2: Connected`** (green).
3. Verify that **Server 1** shows **`Running on Port 5001`** (or 5000 if unoccupied).

### Step 3: Connect External HMI / SCADA

#### Example: Weintek EasyBuilder Pro
1. In EasyBuilder Pro, open **System Parameters** → **New Device**.
2. Configure the device settings:
   - **PLC Type**: `Mitsubishi Q/QnA (Ethernet)` or `Mitsubishi FX3U (Ethernet)`
   - **Interface**: `Ethernet`
   - **IP Address**: `127.0.0.1` (or your host IP if running from an external PC)
   - **Port**: `5001` (match the port shown on NetToGXSim2 Server 1)
   - **Protocol**: `TCP/IP, Binary Mode`
3. Launch **On-line Simulation** in EasyBuilder Pro.
4. The HMI will immediately connect, identify the CPU type, and begin reading/writing tags with zero delay.

#### Example: Python (pymcprotocol)
```python
import pymcprotocol

# Initialize QnA 3E client
mc = pymcprotocol.Type3E()
mc.setaccessopt(commtype="binary")
mc.connect("127.0.0.1", 5001)

# Read 10 words from D0 to D9
values = mc.batchread_wordunits(headdevice="D0", readsize=10)
print(f"D0-D9: {values}")

# Write to D0
mc.batchwrite_wordunits(headdevice="D0", values=[1234])
```

---

## 🔧 Troubleshooting

### 1. MELSOFT Application Error `<ES:01808007>`
- **Cause**: GX Works 2 lost communication with the local simulation process (`SimManager.exe`). This occurs if Transfer Setup in GX Works 2 was mistakenly configured as an external Ethernet module, or if the simulation engine was killed.
- **Solution**:
  1. Click **Exit GX Simulator 2** in NetToGXSim2.
  2. In GX Works 2, go to **Debug** → **Stop Simulation**, then **Start/Stop Simulation** again.
  3. Ensure **Connection Destination** in GX Works 2 remains set to **GX Simulator 2**.

### 2. Port 5000 Automatically Changes to 5001
- **Cause**: Port 5000 is reserved or actively listened on by Windows system services (`svchost.exe` / IP Helper & SSDP).
- **Behavior**: This is normal and expected. NetToGXSim2 automatically detects this conflict and increments to port 5001 to prevent connection timeouts. Simply configure your HMI/client to port `5001`.

### 3. "ActUtlType COM component not registered"
- **Cause**: Mitsubishi MELSOFT communication libraries are not installed or are 64-bit only without 32-bit COM registration.
- **Solution**: Re-run the GX Works 2 installer or ensure the 32-bit MELSOFT Environment files (`EnvMEL`) are installed.

---

## 🛠 Building from Source

### Prerequisites
- Visual Studio 2022 or .NET SDK 8.0+
- Target Framework: `.NET Framework 4.7.2` (Platform: `x86`)
- NSIS 3.x (Optional, for building the installer)

### Build Steps
```bash
# Clone the repository
git clone https://github.com/ismaillowkey/Mitsubishi-NetToGXSim2.git
cd Mitsubishi-NetToGXSim2

# Build the WPF project
dotnet build src/NetToGXSim2.Wpf/NetToGXSim2.Wpf.csproj -c Release

# Publish binaries
dotnet publish src/NetToGXSim2.Wpf/NetToGXSim2.Wpf.csproj -c Release -o publish

# Create NSIS Installer (optional)
create_installer.bat
```

---

## 👤 Author & Support

- **Developer**: Ismail Lowkey
- **Repository**: [ismaillowkey/Mitsubishi-NetToGXSim2](https://github.com/ismaillowkey/Mitsubishi-NetToGXSim2)
- **Version**: 0.6.0

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
