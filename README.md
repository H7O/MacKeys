# MacKeys

A lightweight Windows system tray app that remaps keyboard keys for Mac-to-Windows RDP sessions — making your Mac keyboard feel native in Windows.

## The Problem

When you RDP into Windows from a Mac, the modifier keys are in the wrong positions. Command acts as Win, Option acts as Alt — but they're physically swapped compared to a Windows keyboard. MacKeys fixes this with a low-level keyboard hook.

## Key Mappings

| Physical Key (Mac) | Without MacKeys | With MacKeys |
|---|---|---|
| Left Command | Left Win | **Left Alt** |
| Left Option | Left Alt | **Left Win** |
| Right Option | Right Alt | **Right Ctrl** |
| Right Command | Right Win | **Right Alt** |

The left-side swap means **Cmd+Tab** does **Alt+Tab**, **Cmd+W** does **Alt+F4**, etc. — just like you'd expect on a Mac.

## Features

- System tray app with green/gray status icon
- Left-click tray icon to toggle on/off
- Right-click for context menu (Disable/Enable, Exit)
- Persists toggle state across restarts
- No external dependencies — pure .NET + P/Invoke
- Runs elevated (required for the keyboard hook to work across all windows)

## Download

Grab the latest release for your architecture:

**[→ Download from Releases](https://github.com/H7O/MacKeys/releases/latest)**

| File | Architecture |
|---|---|
| `MacKeys-win-x64.zip` | Intel / AMD (most PCs) |
| `MacKeys-win-arm64.zip` | ARM64 (Surface Pro X, etc.) |

Extract the zip and run `MacKeys.exe` — that's it.

## Requirements

- Windows 10/11
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build from Source

```powershell
# Debug build
dotnet build

# Release publish to deploy/release/
.\build.ps1
```

## Run

Run `MacKeys.exe` — it will prompt for Administrator elevation (required so the keyboard hook works even when an elevated window has focus).

The tray icon shows:
- **Green "M"** — remapping active
- **Gray "M"** — remapping disabled

## License

[MIT](LICENSE)
