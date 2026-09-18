# GX2 Bridge (NetToGXSim2)

**Mitsubishi GX Works 2 Simulator Network Protocol Bridge**  
*Version 0.3.2 | Developed by Ismail Lowkey*

[![Platform](https://img.shields.io/badge/Platform-Windows%20x86%20%7C%20x64-blue.svg)]()
[![Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2-purple.svg)]()
[![Target](https://img.shields.io/badge/Target-MELSOFT%20GX%20Simulator%202-red.svg)]()
[![Protocol](https://img.shields.io/badge/Protocol-MC%20Protocol%20(3E%20%26%201E)-green.svg)]()
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)]()

---

## 📖 Overview

**GX2 Bridge** (NetToGXSim2) is a lightweight, high-performance bridge application that connects **Mitsubishi GX Simulator 2** (the simulation engine bundled with **GX Works 2**) to external Ethernet networks via the standard **MELSEC Communication (MC) Protocol**.

Traditionally, Mitsubishi's GX Simulator 2 runs only as an isolated local COM process (`SimManager.exe`), making it impossible for external HMIs, SCADA packages, or IoT gateways to communicate with the simulated PLC without physical hardware. **GX2 Bridge** removes this limitation by hosting dual Ethernet TCP servers that translate MC Protocol requests directly into internal GX Simulator 2 memory reads and writes with sub-millisecond response times.

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
                               │ ActUtlType COM Interface
┌──────────────────────────────▼──────────────────────────────┐
│                  GX2 Bridge (NetToGXSim2)                   │
│  ┌─────────────────────────┐     ┌────────────────────────┐ │
│  │   MC TCP Server 1       │     │   MC TCP Server 2      │ │
│  │   (Default: Port 5001)  │     │   (Default: Port 6000) │ │
│  └────────────┬────────────┘     └───────────┬────────────┘ │
└───────────────┼──────────────────────────────┼──────────────┘
                │ Ethernet TCP (MC Protocol)   │
    ┌───────────┴──────────┬───────────────────┴───────────┐
    │                      │                               │
┌───▼─────────────┐ ┌──────▼───────────────┐ ┌─────────────▼───┐
│  Weintek HMI    │ │    SCADA / Node-RED   │ │  Python / C# /  │
│ (EasyBuilder)   │ │  (Ignition, VTScada) │ │ OPC-UA Server   │
└─────────────────┘ └──────────────────────┘ └─────────────────┘
```

---

## ✨ Features

- **Dual MC Protocol Servers**:
  - **Server 1**: Default port 5000 (auto-increments to 5001 if port 5000 is occupied). Started automatically at launch.
  - **Server 2**: Default port 6000. Stopped by default for secondary clients or testing.
  - Fully independent start/stop controls and editable port inputs when stopped.

- **Intelligent Port Conflict Resolution**:
  - Automatically checks port availability during startup and manual start.
  - If a port is occupied by Windows services (e.g., `svchost.exe` / IP Helper on port 5000) or other software, GX2 Bridge automatically steps up by 1 (`port++`) until an open port is found, updating the UI textbox and status in real-time.

- **Comprehensive MC Protocol Support**:
  - **QnA 3E Binary & ASCII**:
    - `0x0101`: CPU Model Type Read (returns standard `Q02UCPU` / `FX3U` identification, enabling seamless handshake with Weintek EasyBuilder Pro, Pro-face, and other HMI drivers).
    - `0x0401`: Batch Read (Bit & Word units).
    - `0x0403`: Random Read (Word & DWord multi-address reading).
    - `0x1401`: Batch Write (Bit & Word units).
  - **A-1E Binary & ASCII** (Legacy A Series & FX Series):
    - Subcommand `0x00`: Read Bit devices.
    - Subcommand `0x01`: Read Word devices.
    - Subcommand `0x02`: Write Bit devices.
    - Subcommand `0x03`: Write Word devices.
    - Full 12-byte binary frame alignment with zero stream desynchronization.

- **Interactive Hardware Simulation Panel**:
  - **Inputs Rack (X0 - X7)**: 8 interactive toggle switches with blue LED status indicators. Allows toggling PLC inputs directly from the UI to test ladder logic.
  - **Outputs Rack (Y0 - Y7)**: 8 glowing green LED indicator lamps that reflect the virtual PLC coil logic in real-time.

- **Direct Register Inspector**:
  - Inspect any PLC device address (e.g., `D0`, `M100`, `Y0`, `X0`) on demand.
  - Write single-word values directly to memory registers without external tools.

- **GX Simulator 2 Process Manager**:
  - Built-in **"Exit GX Simulator 2"** feature to cleanly terminate background simulator processes (`SimManager.exe` and `IOSystem.exe`) without needing Windows Task Manager.

---

## 📋 System Requirements

| Requirement | Specification |
| :--- | :--- |
| **Operating System** | Windows 7 / 8 / 10 / 11 (32-bit or 64-bit) |
| **Runtime** | .NET Framework 4.7.2 or higher |
| **PLC Software** | Mitsubishi **GX Works 2** (with GX Simulator 2 installed) |
| **COM Components** | MELSOFT Communication Library (`ActUtlType.ActUtlType`) |

> [!IMPORTANT]
> **GX Simulator 2 vs GX Simulator 3:**  
> This software is specifically engineered for **GX Simulator 2** (bundled with **GX Works 2**). It is **not** compatible with GX Works 3 / GX Simulator 3, which uses a completely different communication architecture.

---

## 🚀 Quick Start Guide

### Step 1: Start Simulation in GX Works 2
1. Open your project ladder logic in **GX Works 2**.
2. Start the simulation: navigate to **Debug** → **Start/Stop Simulation**.
3. Ensure the simulation window (`SimManager.exe`) appears and GX Works 2 is in **Monitor Mode**.

> [!WARNING]
> **Do NOT alter Transfer Setup in GX Works 2!**  
> In GX Works 2 under **Connection Destination → Transfer Setup**, keep the connection set to **GX Simulator 2** (default). Do not change it to Ethernet Board. GX Works 2 communicates with the simulator internally; only your external devices (HMI/SCADA) communicate via Ethernet with GX2 Bridge.

### Step 2: Launch GX2 Bridge
1. Run `NetToGXSim2.Wpf.exe` (or install via `Setup_NetToGXSim2_v0.3.1.exe`).
2. Verify that the status header badge shows **`GX Sim 2: Connected`** (green).
3. Verify that **Server 1** shows **`Running on Port 5001`** (or 5000 if unoccupied).

### Step 3: Connect External HMI / SCADA

#### Example: Weintek EasyBuilder Pro
1. In EasyBuilder Pro, open **System Parameters** → **New Device**.
2. Configure the device settings:
   - **PLC Type**: `Mitsubishi Q/QnA (Ethernet)` or `Mitsubishi FX3U (Ethernet)`
   - **Interface**: `Ethernet`
   - **IP Address**: `127.0.0.1` (or your host IP if running from an external PC)
   - **Port**: `5001` (match the port shown on GX2 Bridge Server 1)
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
  1. Click **Exit GX Simulator 2** in GX2 Bridge.
  2. In GX Works 2, go to **Debug** → **Stop Simulation**, then **Start/Stop Simulation** again.
  3. Ensure **Connection Destination** in GX Works 2 remains set to **GX Simulator 2**.

### 2. Port 5000 Automatically Changes to 5001
- **Cause**: Port 5000 is reserved or actively listened on by Windows system services (`svchost.exe` / IP Helper & SSDP).
- **Behavior**: This is normal and expected. GX2 Bridge automatically detects this conflict and increments to port 5001 to prevent connection timeouts. Simply configure your HMI/client to port `5001`.

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
git clone https://github.com/ismaillowkey/NetToGX2Sim.git
cd NetToGX2Sim

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
- **Repository**: [ismaillowkey/NetToGX2Sim](https://github.com/ismaillowkey/NetToGX2Sim)
- **Version**: 0.3.1

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
