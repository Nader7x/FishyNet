# FishyNet (Modern .NET 10 WPF Edition)

High-performance LAN bandwidth shaper, real-time traffic analyzer, and historical device usage monitor for Windows (`net10.0-windows`, `x64`).

## Key Capabilities

- **Zero-Allocation Native `wpcap.dll` (Npcap) Packet Engine**: Directly wraps Npcap kernel ring buffers using `.NET 10` `LibraryImport` and `Span<byte>` for zero-copy packet inspection and in-place Ethernet header rewriting.
- **Human-Friendly Speed & Volume Representations**: Auto-scales throughput (`B/s`, `KB/s`, `MB/s`, `GB/s`) with a one-click toggle to ISP bit-rates (`bps`, `Kbps`, `Mbps`, `Gbps`), proportional visual utilization bars, and `% of Cap` indicators.
- **Precision Token-Bucket Rate Limiter & Presets**: Smooth sub-millisecond token-bucket bandwidth shaping (eliminating legacy 1-second burst-stall cutoffs) with one-click presets (**Unrestricted**, **Gaming Priority**, **SD Streaming**, **Strict Throttle**, **Quarantine**).
- **Historical Device Usage Ledger (Today / Past Week / Past Month / All-Time)**: Persistent MAC-indexed daily usage ledger (`%LocalAppData%\FishyNet\usage_history.json`) with interactive stacked daily bar charts, LAN traffic share percentages, daily data quotas, and CSV export.
- **Background System Tray Operation**: Minimize or close to the Windows System Tray with live throughput tooltips, toast notifications, and a right-click quick-action menu.
- **Advanced Network & Security Tools**:
  - Embedded IEEE OUI MAC vendor lookup & async hostname resolution
  - Live per-device DNS query inspector & domain blocker
  - Continuous ICMP Ping RTT monitor & Wake-on-LAN (WoL) Magic Packet sender
  - Passive ARP Spoof / MITM security guard

## Requirements

- **Windows 10 / 11 (x64)**
- **[Npcap](https://npcap.com/)** installed (with WinPcap API-compatible mode enabled)
- **Administrator Privileges** (configured automatically via `app.manifest`)

## Build & Test

```powershell
# Build solution
dotnet build FishyNet.sln -c Release

# Run automated tests
dotnet test FishyNet.sln -c Release
```

## Build MSI Installer

FishyNet includes a WiX v7-based MSI installer pipeline that produces a 64-bit per-machine installer with Start Menu and Desktop shortcuts, automated single-file harvesting, and standard Windows Installer UI dialogs.

```powershell
# Build self-contained release and compile MSI package to artifacts/
pwsh .\build-installer.ps1 -Configuration Release -OutputDir artifacts
```

## CI/CD & Releases

- **Continuous Integration (`.github/workflows/ci.yml`)**: Automatically triggers on pushes and pull requests to `main` to build the solution, execute tests, and validate the WiX MSI installer package.
- **GitHub Releases (`.github/workflows/release.yml`)**: Automatically publishes full releases on GitHub when a tag matching `v*` (e.g. `v3.2.0`) is pushed, or via manual `workflow_dispatch`. Generates both the standalone MSI installer and a portable zip archive accompanied by SHA-256 checksums.
