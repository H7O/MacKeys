// MacKeys — Mac-to-Windows keyboard remapper for RDP sessions
//
// Key mappings (when active):
//   Left Win  (0x5B) ↔ Left Alt  (0xA4)   Swap — puts Cmd/Option in Windows positions
//   Right Alt (0xA5) → Right Ctrl (0xA3)   Mac Right Option → Right Ctrl
//   Right Win (0x5C) → Left Alt  (0xA4)    Mac Right Cmd    → Left Alt
//
// The left-side swap makes Command land where Alt is physically on a Windows keyboard
// (and vice versa), so Alt+Tab, Alt+F4, etc. work from the key you'd expect.
// The right-side remaps produce the standard Windows right modifier layout.
//
// Right Cmd intentionally maps to VK_LMENU (Left Alt) rather than VK_RMENU (Right Alt).
// On non-US keyboard layouts, VK_RMENU is AltGr (= Ctrl+Alt), so injecting it breaks
// Alt+key combos (apps see Ctrl+Alt+key). VK_LMENU is always plain Alt with no AltGr.
//
// Injected keystrokes carry the LLKHF_INJECTED flag set by the OS, which the hook
// checks to avoid re-intercepting its own output (prevents infinite loops).
//
// Staying in step with Windows
// ----------------------------
// Windows gives a low-level keyboard hook a short deadline for every keystroke
// (LowLevelHooksTimeout, about 300 ms unless overridden in the registry). If the hook
// answers late — the machine is thrashing, this process was paged out while the RDP
// session sat disconnected, another keyboard hook in the chain is slow — Windows carries
// on without it, and it may then drop the hook for good without telling the app. Either
// way a keystroke reaches Windows un-remapped while this app believes it handled it.
// Separately, when the desktop changes (lock screen, UAC prompt, RDP disconnect) the
// key-up for a held modifier goes to a desktop this hook cannot see.
//
// Four things stop those from turning into a modifier that stays down:
//
//   1. A key-up is only swallowed if Windows does not think that key is down. If the
//      matching key-down slipped past the hook, the key-up is let through so Windows
//      releases the key. (See the key-up branch in HookCallback.)
//   2. A timer watches Windows' own key state. A remapped key going down there while the
//      hook saw no events at all means Windows went on without the hook. The timer then
//      sends a key-up for a key that does not exist and checks whether the hook sees it.
//      If it does, the hook only answered too late that once, and it is left alone. If it
//      does not, Windows has stopped calling the hook, and the hook is put back. (See
//      OnTick, HookAnswers.) The hook is never reinstalled without that proof, because
//      reinstalling moves it ahead of every other keyboard hook, and the order matters to
//      tools like the Remote Desktop client and PowerToys.
//   3. When the desktop or session changes, every modifier this app is holding down is
//      released. (See OnTick, _resyncPending.)
//   4. When the timer itself fires much too late, this app did not get to run for a while,
//      so the key-up of a modifier it is holding down may have gone by unseen. That
//      modifier is released. If the key is in fact still held, it has to be pressed again,
//      which is the lesser evil. (See OnTick, StallMs.)

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

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
    const ushort VK_PROBE    = 0xE8;   // unassigned: no keyboard, layout or app gives it a meaning

    const uint INPUT_KEYBOARD = 1;

    const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x0020;
    const uint WINEVENT_OUTOFCONTEXT      = 0x0000;

    // ── P/Invoke ─────────────────────────────────────────────────────────

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                               int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
                                         WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

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

    static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // ── Key map ──────────────────────────────────────────────────────────

    // Source[i] is the key that arrives from the Mac keyboard; Target[i] is injected in
    // its place. Two sources may share a target (both Cmd keys become Left Alt).
    static readonly ushort[] Source     = [VK_LWIN,    VK_LMENU,      VK_RMENU,       VK_RWIN];
    static readonly ushort[] Target     = [VK_LMENU,   VK_LWIN,       VK_RCONTROL,    VK_LMENU];
    static readonly string[] SourceName = ["Left Cmd", "Left Option", "Right Option", "Right Cmd"];

    static int SourceIndex(uint vk) => vk switch
    {
        VK_LWIN  => 0,
        VK_LMENU => 1,
        VK_RMENU => 2,
        VK_RWIN  => 3,
        _        => -1
    };

    // ── Application state ────────────────────────────────────────────────

    // Everything below is only touched on the UI thread (which is also the thread Windows
    // calls the hook on), except _resyncPending, which other threads may set.

    static IntPtr _hookId = IntPtr.Zero;
    static readonly LowLevelKeyboardProc _hookProc = HookCallback; // prevent GC
    static readonly WinEventProc _desktopSwitchProc = OnDesktopSwitch; // prevent GC

    // _held[i]: the key-down of Source[i] was swallowed and Target[i] injected for it, and
    // the matching key-up has not been handled yet.
    static readonly bool[] _held = new bool[4];

    // Watchdog bookkeeping (see OnTick).
    const int WatchIntervalMs = 150;
    const int StallMs         = 500;                     // a tick this late: the thread was not running
    static System.Windows.Forms.Timer _watch = null!;
    static long _lastTickAt;                             // Environment.TickCount64 at the previous tick
    static uint _callbacks;                              // hook invocations, ever
    static uint _callbacksAtTick;                        // _callbacks as of the previous tick
    static readonly bool[] _rawAtTick = new bool[4];     // IsRawDown(i) as of the previous tick
    static volatile bool _resyncPending;                 // desktop or session changed
    static int _repairs;                                 // times the hook had to be put back

    static NotifyIcon          _trayIcon  = null!;
    static ToolStripMenuItem   _toggleItem = null!;
    static bool                _enabled;
    static Icon                _iconOn  = null!;
    static Icon                _iconOff = null!;

    static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    static string LogPath      => Path.Combine(AppContext.BaseDirectory, "MacKeys.log");

    // ── Entry point ──────────────────────────────────────────────────────

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The hook must answer every keystroke on the system inside Windows' deadline and
        // does almost no work, so let it run ahead of ordinary-priority work such as builds.
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
            // Best-effort.
        }

        _iconOn  = CreateIcon(Color.FromArgb(34, 197, 94));   // green
        _iconOff = CreateIcon(Color.FromArgb(120, 120, 120));  // gray

        _toggleItem = new ToolStripMenuItem("Enable");
        _toggleItem.Click += (_, _) => Toggle();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            Disable();
            _trayIcon.Visible = false;
            Application.Exit();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon             = _iconOff,
            Text             = "MacKeys: OFF",
            ContextMenuStrip = menu,
            Visible          = true
        };

        // Left-click toggles on/off for quick access.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };

        _watch = new System.Windows.Forms.Timer { Interval = WatchIntervalMs };
        _watch.Tick += (_, _) => OnTick();

        // Both of these only raise a flag; OnTick does the work on the UI thread.
        SystemEvents.SessionSwitch += (_, _) => _resyncPending = true;
        SetWinEventHook(EVENT_SYSTEM_DESKTOPSWITCH, EVENT_SYSTEM_DESKTOPSWITCH, IntPtr.Zero,
                        _desktopSwitchProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        _enabled = LoadEnabled() && Enable();
        UpdateTray();

        Application.Run();
    }

    // ── Toggle / Enable / Disable ────────────────────────────────────────

    static void Toggle()
    {
        bool enable = !_enabled;

        if (!enable) Disable();

        // Written while the hook is not installed, so this thread is never doing disk I/O
        // at a moment when it has keystrokes to answer.
        SaveEnabled(enable);

        _enabled = enable && Enable();
        UpdateTray();
    }

    static bool Enable()
    {
        if (!InstallHook())
        {
            MessageBox.Show(
                "Failed to install keyboard hook.\nMake sure the app is running as Administrator.",
                "MacKeys", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _lastTickAt = Environment.TickCount64;
        _watch.Start();
        return true;
    }

    static void Disable()
    {
        _watch.Stop();
        RemoveHook();

        // Release any injected modifiers still logically held down.
        // Without this, toggling off (or exiting) while a physical modifier is held
        // leaves the injected key-down with no matching key-up, permanently sticking it.
        ReleaseAll();
        Array.Clear(_held);
        _resyncPending = false;
    }

    static bool InstallHook()
    {
        if (_hookId == IntPtr.Zero)
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);

        SnapshotRaw();
        return _hookId != IntPtr.Zero;
    }

    static void RemoveHook()
    {
        if (_hookId == IntPtr.Zero) return;

        // Fails if Windows already dropped the hook; there is nothing to do about that.
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    static void UpdateTray()
    {
        _trayIcon.Icon = _enabled ? _iconOn : _iconOff;
        _trayIcon.Text = !_enabled     ? "MacKeys: OFF"
                       : _repairs == 0 ? "MacKeys: ON"
                       :                 $"MacKeys: ON (hook repaired {_repairs}x, see MacKeys.log)";
        _toggleItem.Text = _enabled ? "Disable" : "Enable";
    }

    // ── Hook callback (hot path — no allocations, no blocking) ───────────

    static unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            _callbacks++;
            var kbd = (KBDLLHOOKSTRUCT*)lParam;

            // Skip injected keystrokes, our own included (OS-level flag).
            if ((kbd->flags & LLKHF_INJECTED) == 0)
            {
                int i = SourceIndex(kbd->vkCode);
                if (i >= 0)
                {
                    int msg = (int)wParam;

                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                    {
                        _held[i] = true;
                        SendKey(Target[i], keyUp: false);
                        return (IntPtr)1; // suppress the original keystroke
                    }

                    if (msg is WM_KEYUP or WM_SYSKEYUP)
                    {
                        if (_held[i])
                        {
                            _held[i] = false;
                            // The other Cmd key may still be holding the same target down.
                            if (!TargetHeld(Target[i])) SendKey(Target[i], keyUp: true);
                        }

                        // Normally Windows never saw this key go down, so its key-up is
                        // swallowed too. If Windows does think it is down, the key-down got
                        // past us (we answered too late and Windows went on without us), and
                        // swallowing the key-up would leave the key stuck down for good.
                        if (!IsRawDown(i)) return (IntPtr)1;
                    }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // True if some key currently held on the Mac side is keeping the injected key vk down.
    static bool TargetHeld(ushort vk)
    {
        for (int i = 0; i < Target.Length; i++)
            if (_held[i] && Target[i] == vk) return true;
        return false;
    }

    // True if Windows believes Source[i] is down for a reason other than our own injection
    // (Left Win and Left Alt are both a source and a target). With a working hook that only
    // happens when someone else injects the key or a key-down slipped past us.
    static bool IsRawDown(int i) => GetAsyncKeyState(Source[i]) < 0 && !TargetHeld(Source[i]);

    // ── Watchdog ─────────────────────────────────────────────────────────

    static void OnDesktopSwitch(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                                int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
        => _resyncPending = true;

    static void OnTick()
    {
        long now = Environment.TickCount64;
        long gap = now - _lastTickAt;
        _lastTickAt = now;

        if (_hookId == IntPtr.Zero)
        {
            InstallHook(); // an automatic reinstall failed earlier; keep trying
            return;
        }

        if (_resyncPending)
        {
            // Lock screen, UAC prompt, RDP disconnect/reconnect: key-ups typed on the other
            // desktop never reach this hook, so anything we hold down would stay down.
            // SendInput fails while our desktop is not the active one, in which case
            // ReleaseAll leaves the keys marked as held and this runs again next tick.
            bool any = AnyHeld();
            if (ReleaseAll())
            {
                _resyncPending = false;
                if (any) Log("Released the modifiers MacKeys was holding down, because the desktop or session changed while they were held.");
            }

            SnapshotRaw();
            return;
        }

        // A live hook is called for every key event before Windows updates its key state,
        // so a remapped key cannot go down in that state without at least one callback.
        // If it did, Windows went on without the hook: for that one keystroke, because the
        // hook answered too late, or for good.
        uint calls = _callbacks;
        int leaked = -1;

        for (int i = 0; i < Source.Length; i++)
        {
            bool raw = IsRawDown(i);
            if (raw && !_rawAtTick[i] && calls == _callbacksAtTick) leaked = i;
            _rawAtTick[i] = raw;
        }

        if (leaked >= 0)
        {
            if (HookAnswers())
                Log($"{SourceName[leaked]} reached Windows without being remapped, because MacKeys answered Windows too late for that keystroke. " +
                    "The hook is still being called, so it was left as it is.");
            else
                Reinstall(leaked);
        }

        // This thread was held up for longer than Windows waits for a hook, so a key-up may
        // have gone by without the hook being called. Whatever is still marked as held
        // could then stay down for good, so it is let go.
        if (gap > StallMs && AnyHeld())
        {
            if (!ReleaseAll()) _resyncPending = true; // Windows refused; retry on later ticks
            Log($"MacKeys could not run for {gap} ms while it was holding a modifier down, so the key-up may have been missed. The modifier was released.");
        }

        // Taken last, so the test keystroke and the key-ups sent above do not count as the
        // hook having seen a key.
        _callbacksAtTick = _callbacks;
    }

    // True if Windows still calls the hook. Sends a key-up for a key that does not exist:
    // a keystroke injected by the hook's own thread goes through the hook before SendInput
    // returns, so a live hook has counted it by then.
    static bool HookAnswers()
    {
        uint before = _callbacks;

        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk     = VK_PROBE;
        input.u.ki.dwFlags = KEYEVENTF_KEYUP;

        // If Windows refuses the keystroke there is no telling. The next leaked key asks again.
        if (SendInput(1, ref input, InputSize) != 1) return true;

        return _callbacks != before;
    }

    static void SnapshotRaw()
    {
        for (int i = 0; i < Source.Length; i++) _rawAtTick[i] = IsRawDown(i);
        _callbacksAtTick = _callbacks;
    }

    static void Reinstall(int leakedSource)
    {
        RemoveHook();

        // Key-ups may have been missed while the hook was not being called, so what we
        // think we are holding down can no longer be trusted.
        ReleaseAll();
        Array.Clear(_held);

        bool ok = InstallHook();

        _repairs++;
        UpdateTray();
        Log($"{SourceName[leakedSource]} reached Windows without being remapped, and the hook did not see a test keystroke either, so Windows had stopped calling it. " +
            (ok ? "Hook reinstalled." : "Reinstalling failed; will keep retrying."));
    }

    // ── Injection ────────────────────────────────────────────────────────

    static bool AnyHeld()
    {
        for (int i = 0; i < _held.Length; i++)
            if (_held[i]) return true;
        return false;
    }

    // Sends a key-up for every injected modifier that is still down. Returns false if
    // Windows refused any of them; those stay marked as held so the caller can retry.
    static bool ReleaseAll()
    {
        bool ok = true;

        for (int i = 0; i < _held.Length; i++)
        {
            if (!_held[i]) continue;

            _held[i] = false;
            if (TargetHeld(Target[i])) continue; // released when the other source's turn comes

            if (!SendKey(Target[i], keyUp: true))
            {
                _held[i] = true;
                ok = false;
            }
        }

        return ok;
    }

    static bool SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk    = vk;
        input.u.ki.wScan  = ScanCode(vk);
        input.u.ki.dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0u)
                           | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);

        return SendInput(1, ref input, InputSize) == 1;
    }

    // Scan codes for the keys we inject. Other hooks and some apps (Windows Terminal
    // included) read wScan from the KBDLLHOOKSTRUCT; a zero scan code can prevent
    // the Alt key from being recognised as a modifier.
    static ushort ScanCode(ushort vk) => vk switch
    {
        VK_LMENU    => 0x38,   // Alt (left, non-extended)
        VK_LWIN     => 0x5B,   // Left Win (extended)
        VK_RCONTROL => 0x1D,   // Right Ctrl (extended)
        _           => 0
    };

    // VK_LMENU (Left Alt) is NOT an extended key — it has its own dedicated scan code.
    // VK_RMENU is no longer injected (Right Cmd maps to VK_LMENU to avoid AltGr).
    static bool IsExtended(ushort vk) => vk is VK_LWIN or VK_RWIN or VK_RCONTROL;

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

    // ── Log ──────────────────────────────────────────────────────────────

    // Records the rare occasions the watchdog had to step in, so there is something to
    // look at afterwards. Written on a pool thread: the caller is the hook's thread.
    static readonly object _logLock = new();

    static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";

        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                lock (_logLock)
                {
                    var file = new FileInfo(LogPath);
                    if (file.Exists && file.Length > 256 * 1024) file.Delete();
                    File.AppendAllText(LogPath, (string)state!);
                }
            }
            catch
            {
                // Best-effort.
            }
        }, line);
    }
}
