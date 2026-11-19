# FluxDesk

Lightweight, private desktop workspace manager for Windows 10 & 11 with native shell redirection and zero telemetry.

## Features

- **Native Shell Redirection**: Official Windows Known Folder API integration (`SHSetKnownFolderPath`).
- **Physical Workspace Isolation**: Each desktop is an independent directory on disk.
- **Pixel-Accurate Icon Memory**: Restores custom icon layouts per workspace via ListView memory inspection.
- **Low-Latency Switcher**: Hardware-accelerated quick switcher (`Alt + W`) with zero focus stealing in games.
- **Hotkeys**: Customizable switching hotkeys (`Alt + 1` through `Alt + 9`).
- **Data Protection**: Safe deletion with optional Recycle Bin support; automatic primary desktop restoration on system shutdown.
- **Zero Telemetry**: Fully offline, local, and private.

## Architecture

- `FluxDesk.UI`: Modern WPF configuration window and workspace management dashboard.
- `FluxDesk.Daemon`: Background WinForms worker process hosting low-level keyboard hooks, icon layout restoration, and shell redirection.

## Building from Source

Requirements:
- .NET 8.0 SDK (x64)
- Windows 10 (version 1809+) or Windows 11

```powershell
# Clone repository
git clone https://github.com/l1vensi/FluxDesk.git
cd FluxDesk\src

# Build solution
dotnet build FluxDesk.sln -c Release
```

## Contributors

- [l1vensi](https://github.com/l1vensi) — Author & Lead Developer
- [gemini-code-assist](https://github.com/apps/gemini-code-assist) — Co-author & AI Pair Programmer

## License

This software is released under open-source terms. See the LICENSE file for details.
