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
- Recovers by itself when Windows goes on without its keyboard hook, instead of leaving a modifier stuck down (see [Self-repair](#self-repair))

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
- The release downloads include the .NET runtime and need nothing else
- Building from source needs the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0). What `build.ps1` produces does not include the runtime, so it needs the .NET 11 Desktop Runtime from the same page

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

## Self-repair

Windows gives a keyboard hook a short deadline for every keystroke. If MacKeys answers late — the PC is under heavy load, or it was paged out while the RDP session sat disconnected — Windows lets that keystroke through un-remapped, and may stop calling the hook altogether without telling the app. MacKeys handles this in four ways:

- A key-up is let through whenever Windows thinks that key is down, so a key-down that slipped past the hook can never stay stuck.
- A watchdog notices a remapped key reaching Windows while the hook saw nothing. It then checks whether Windows still calls the hook, and reinstalls the hook only if it does not. After a reinstall the tray tooltip reads `MacKeys: ON (hook repaired 1x, see MacKeys.log)`.
- When you lock the PC, answer a UAC prompt, or disconnect and reconnect the RDP session, every modifier MacKeys is holding down is released.
- When MacKeys itself could not run for more than half a second, it releases every modifier it is holding down, because it may have missed the key-up. If you were still holding the key, press it again.

Whenever the watchdog steps in or modifiers are released this way, a line is written to `MacKeys.log` next to the executable. The file stays absent as long as nothing went wrong.

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
