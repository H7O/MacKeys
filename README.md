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
| Right Command | Right Win | **Left Alt** |

The left-side swap means **Cmd+Tab** does **Alt+Tab**, **Cmd+W** does **Alt+F4**, etc. — just like you'd expect on a Mac.

> Right Cmd maps to **Left Alt** (not Right Alt) on purpose — on non-US keyboard layouts, Right Alt is treated as AltGr (= Ctrl+Alt), which breaks Alt+key combos. Left Alt is always plain Alt.

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

## Important: Windows App (Microsoft Remote Desktop) on Mac

If you connect from Mac using the **Windows App** (formerly Microsoft Remote Desktop), it ships with a feature that intercepts `⌘C`, `⌘V`, `⌘X`, `⌘A`, `⌘Z`, and `⌘F` and translates them to `Ctrl+C/V/X/A/Z/F` **before** keystrokes reach Windows. This bypasses MacKeys entirely — Right Cmd combos won't get remapped because they never arrive as Cmd on the Windows side.

**To fix this:**

1. Open **Windows App → Settings → Keyboard**
2. Find the **"Redirect shortcuts when using"** dropdown
3. Set it to **"Left Command (⌘) key"** (not "Either")
    - This lets Left Cmd still auto-translate to Ctrl for familiar Mac-style copy/paste
    - Right Cmd now passes through raw, so MacKeys can remap it to Alt
4. **Disconnect and reconnect the RDP session** — the setting change does not take effect on the current session

If you'd rather MacKeys handle *all* Cmd keys (both sides), set the dropdown to **"None"** or disable each individual toggle (Copy, Cut, Paste, Select All, Undo, Find).

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
