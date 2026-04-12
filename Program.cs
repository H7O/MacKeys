// MacKeys — Mac-to-Windows keyboard remapper for RDP sessions
//
// Key mappings (when active):
//   Left Win  (0x5B) ↔ Left Alt  (0xA4)   Swap — puts Cmd/Option in Windows positions
//   Right Alt (0xA5) → Right Ctrl (0xA3)   Mac Right Option → Right Ctrl
//   Right Win (0x5C) → Right Alt  (0xA5)   Mac Right Cmd    → Right Alt
//
// The left-side swap makes Command land where Alt is physically on a Windows keyboard
// (and vice versa), so Alt+Tab, Alt+F4, etc. work from the key you'd expect.
// The right-side remaps produce the standard Windows right modifier layout.
//
// Injected keystrokes carry the LLKHF_INJECTED flag set by the OS, which the hook
// checks to avoid re-intercepting its own output (prevents infinite loops).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace MacKeys;

static class Program
{
    // ── Win32 constants ──────────────────────────────────────────────────

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN    = 0x0100;
    const int WM_KEYUP      = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP   = 0x0105;

    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP       = 0x0002;
    const uint LLKHF_INJECTED        = 0x00000010;

    const ushort VK_LWIN     = 0x5B;
    const ushort VK_RWIN     = 0x5C;
    const ushort VK_LMENU    = 0xA4;
    const ushort VK_RMENU    = 0xA5;
    const ushort VK_RCONTROL = 0xA3;

    const uint INPUT_KEYBOARD = 1;

    // ── P/Invoke ─────────────────────────────────────────────────────────

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(IntPtr handle);

    // ── Interop structs ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint   vkCode;
        public uint   scanCode;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    // MOUSEINPUT is only declared so the INPUTUNION has the correct native size.
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int    dx;
        public int    dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint       type;
        public INPUTUNION u;
    }

    // ── Application state ────────────────────────────────────────────────

    static IntPtr              _hookId    = IntPtr.Zero;
    static readonly LowLevelKeyboardProc _hookProc = HookCallback; // prevent GC
    static NotifyIcon          _trayIcon  = null!;
    static ToolStripMenuItem   _toggleItem = null!;
    static bool                _enabled;
    static Icon                _iconOn  = null!;
    static Icon                _iconOff = null!;

    static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    // ── Entry point ──────────────────────────────────────────────────────

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _iconOn  = CreateIcon(Color.FromArgb(34, 197, 94));   // green
        _iconOff = CreateIcon(Color.FromArgb(120, 120, 120));  // gray

        _enabled = LoadEnabled();

        _toggleItem = new ToolStripMenuItem(_enabled ? "Disable" : "Enable");
        _toggleItem.Click += (_, _) => Toggle();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            Unhook();
            _trayIcon.Visible = false;
            Application.Exit();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon             = _enabled ? _iconOn : _iconOff,
            Text             = _enabled ? "MacKeys: ON" : "MacKeys: OFF",
            ContextMenuStrip = menu,
            Visible          = true
        };

        // Left-click toggles on/off for quick access.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };

        if (_enabled) Hook();

        Application.Run();
    }

    // ── Toggle / Hook / Unhook ───────────────────────────────────────────

    static void Toggle()
    {
        _enabled = !_enabled;

        if (_enabled) Hook(); else Unhook();

        _trayIcon.Icon = _enabled ? _iconOn : _iconOff;
        _trayIcon.Text = _enabled ? "MacKeys: ON" : "MacKeys: OFF";
        _toggleItem.Text = _enabled ? "Disable" : "Enable";

        SaveEnabled(_enabled);
    }

    static void Hook()
    {
        if (_hookId != IntPtr.Zero) return;

        using var proc = Process.GetCurrentProcess();
        using var mod  = proc.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            MessageBox.Show(
                "Failed to install keyboard hook.\nMake sure the app is running as Administrator.",
                "MacKeys", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void Unhook()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    // ── Hook callback (hot path — no allocations, no blocking) ───────────

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Skip keystrokes we injected ourselves (OS-level flag).
            if ((kbd.flags & LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool isUp   = msg is WM_KEYUP   or WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    ushort? replacement = (ushort)kbd.vkCode switch
                    {
                        VK_LWIN  => VK_LMENU,     // Left Cmd  → Left Alt
                        VK_LMENU => VK_LWIN,      // Left Opt  → Left Win
                        VK_RMENU => VK_RCONTROL,   // Right Opt → Right Ctrl
                        VK_RWIN  => VK_RMENU,      // Right Cmd → Right Alt
                        _        => null
                    };

                    if (replacement is ushort vk)
                    {
                        SendKey(vk, isUp);
                        return (IntPtr)1; // suppress the original keystroke
                    }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ── SendInput helper ─────────────────────────────────────────────────

    static void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk    = vk;
        input.u.ki.dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0u)
                           | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    static bool IsExtended(ushort vk) => vk is VK_LWIN or VK_RWIN or VK_RMENU or VK_RCONTROL;

    // ── Tray icon generation ─────────────────────────────────────────────

    static Icon CreateIcon(Color fill)
    {
        using var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, 0, 0, 15, 15);

        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var sf   = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("M", font, Brushes.White, new RectangleF(0, 0, 16, 16), sf);

        // Icon.FromHandle does NOT own the HICON, so clone to a managed Icon
        // and destroy the native handle immediately.
        IntPtr hIcon = bmp.GetHicon();
        using var tmp = Icon.FromHandle(hIcon);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    // ── Settings persistence ─────────────────────────────────────────────

    static bool LoadEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return true;
            var json = File.ReadAllText(SettingsPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("enabled", out var prop) && prop.GetBoolean();
        }
        catch
        {
            return true; // default ON
        }
    }

    static void SaveEnabled(bool enabled)
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { enabled }));
        }
        catch
        {
            // Best-effort; don't crash for a settings write failure.
        }
    }
}
