using System.Runtime.InteropServices;

namespace StrykerOSS.Core.Services;

/// <summary>
/// REAL DuckyScript (Hak5) parser + executor. Injects real keystrokes into THIS
/// Windows session via the Win32 SendInput API - no simulation, no stubs.
/// STRING text is mapped through the ACTIVE keyboard layout (VkKeyScanW), so
/// non-US layouts (e.g. FR AZERTY) type correctly. Educational use only.
/// </summary>
public sealed class HidService : IHidService
{
    private readonly ILogger _log;
    private readonly object _runLock = new();
    private readonly object _heldLock = new();
    private readonly List<ushort> _heldKeys = new();     // modifiers currently held down (for safety release)
    private CancellationTokenSource? _cts;
    private Task _runTask = Task.CompletedTask;
    private volatile bool _running;

    public bool IsRunning => _running;

    public HidService(ILogger log) => _log = log;

    // ============================ IHidService ============================

    public async Task<HidScriptInfo> ParseAsync(string duckPath)
    {
        if (string.IsNullOrWhiteSpace(duckPath)) throw new ArgumentException("Script path required", nameof(duckPath));
        if (!File.Exists(duckPath)) throw new FileNotFoundException("DuckyScript not found", duckPath);

        const int defDelay = 10;                                       // DEFAULTDELAY applied between commands
        var lines = await ReadLinesAsync(duckPath).ConfigureAwait(false);
        int commands = 0, chars = 0, delays = 0;
        bool inBlock = false;
        foreach (var rawLine in lines)
        {
            var raw = rawLine.Trim();
            if (inBlock) { if (raw.Length == 0) inBlock = false; continue; }
            if (raw.Length == 0) continue;
            int sp = raw.IndexOf(' ');
            var cmd = (sp < 0 ? raw : raw[..sp]).ToUpperInvariant();
            var arg = sp < 0 ? "" : raw[(sp + 1)..].Trim();
            switch (cmd)
            {
                case "REM":
                    if (arg.Equals("BLOCK", StringComparison.OrdinalIgnoreCase)) inBlock = true;
                    break;
                case "DELAY":
                    if (int.TryParse(arg, out int ms) && ms > 0) delays += ms; else commands++;
                    break;
                case "STRING":
                    chars += arg.Length; commands++;
                    break;
                case "STRINGLN":
                    chars += arg.Length; commands++;
                    break;
                default:
                    commands++;
                    break;
            }
        }
        double totalMs = commands * defDelay + chars * 8.0 + delays;
        return new HidScriptInfo(Path.GetFileName(duckPath), lines.Length, chars, TimeSpan.FromMilliseconds(totalMs));
    }

    public Task RunAsync(string duckPath, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(duckPath)) throw new FileNotFoundException("DuckyScript not found", duckPath);
        Task t;
        lock (_runLock)
        {
            if (_running) throw new InvalidOperationException("A DuckyScript is already running - stop it first.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = linked;
            _running = true;
            _runTask = RunCoreAsync(duckPath, log, linked);
            t = _runTask;
        }
        return t;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_runLock) { cts = _cts; }
        if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        try { await _runTask.ConfigureAwait(false); } catch { /* cancellation/fault is surfaced to the run caller */ }
        ReleaseAllHeld();
    }

    // ============================ interpreter ============================

    private async Task RunCoreAsync(string duckPath, IProgress<string>? log, CancellationTokenSource linked)
    {
        var ct = linked.Token;
        try
        {
            log?.Report($"> running {Path.GetFileName(duckPath)}");
            _log.Info("hid", $"DuckyScript start: {duckPath}");
            var lines = await ReadLinesAsync(duckPath).ConfigureAwait(false);

            int defaultDelay = 10;                                     // Ducky default is 0 - 10ms keeps hosts reliable
            (string Cmd, string Arg)? last = null;

            for (int i = 0; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = lines[i].Trim();
                if (raw.Length == 0) continue;
                int sp = raw.IndexOf(' ');
                string cmd = (sp < 0 ? raw : raw[..sp]).ToUpperInvariant();
                string arg = sp < 0 ? "" : raw[(sp + 1)..].Trim();

                if (cmd == "REM")
                {
                    if (arg.Equals("BLOCK", StringComparison.OrdinalIgnoreCase))   // REM BLOCK: comment until blank line
                        while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0) i++;
                    continue;
                }
                if (cmd == "DEFAULTDELAY" || cmd == "DEFAULT_DELAY")
                {
                    if (int.TryParse(arg, out int d) && d >= 0) defaultDelay = d;
                    continue;
                }
                if (cmd == "DELAY")
                {
                    if (int.TryParse(arg, out int ms) && ms > 0)
                    {
                        log?.Report($"> DELAY {ms}");
                        await Delay(ct, ms).ConfigureAwait(false);
                        last = ("DELAY", arg);
                    }
                    else log?.Report($"> bad DELAY value '{arg}'");
                    continue;
                }
                if (cmd == "REPEAT")
                {
                    if (last == null) { log?.Report("> REPEAT with no previous command"); continue; }
                    if (!int.TryParse(arg, out int n) || n < 1) n = 1;
                    if (n > 1000) n = 1000;                            // sanity cap
                    for (int r = 0; r < n; r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (last.Value.Cmd == "DELAY" && int.TryParse(last.Value.Arg, out int dms))
                        {
                            log?.Report($"> DELAY {dms}");
                            await Delay(ct, dms).ConfigureAwait(false);
                        }
                        else
                        {
                            await ExecuteCommandAsync(last.Value.Cmd, last.Value.Arg, log, ct).ConfigureAwait(false);
                            await Delay(ct, defaultDelay).ConfigureAwait(false);
                        }
                    }
                    continue;
                }

                await ExecuteCommandAsync(cmd, arg, log, ct).ConfigureAwait(false);
                last = (cmd, arg);
                await Delay(ct, defaultDelay).ConfigureAwait(false);
            }

            log?.Report("> script finished");
            _log.Info("hid", "DuckyScript finished");
        }
        finally
        {
            ReleaseAllHeld();
            _running = false;
        }
    }

    private async Task ExecuteCommandAsync(string cmd, string arg, IProgress<string>? log, CancellationToken ct)
    {
        switch (cmd)
        {
            case "STRING":
                log?.Report($"> STRING {arg}");
                await TypeStringAsync(arg, log, ct).ConfigureAwait(false);
                return;
            case "STRINGLN":
                log?.Report($"> STRINGLN {arg}");
                await TypeStringAsync(arg, log, ct).ConfigureAwait(false);
                Tap(0x0D);                                             // VK_RETURN
                await Delay(ct, 5).ConfigureAwait(false);
                return;
            default:
                // key / combo line: "ENTER", "GUI r", "CTRL-ALT-DELETE", "ALT F4", "CTRL-SHIFT-ESC" ...
                var names = cmd.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(arg.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
                    .ToArray();
                log?.Report($"> {cmd}{(arg.Length > 0 ? " " + arg : "")}");
                await PressComboAsync(names, log, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task PressComboAsync(string[] names, IProgress<string>? log, CancellationToken ct)
    {
        var mods = new List<ushort>();
        var keys = new List<ushort>();
        foreach (var n in names)
        {
            var vk = Vk(n);
            if (vk == null) { log?.Report($"> unknown key '{n}'"); continue; }
            if (IsModifier(n)) { if (!mods.Contains(vk.Value)) mods.Add(vk.Value); }
            else keys.Add(vk.Value);
        }
        if (keys.Count == 0 && mods.Count == 1)
        {
            Tap(mods[0]);                                              // bare modifier = tap
            await Delay(ct, 5).ConfigureAwait(false);
            return;
        }
        foreach (var m in mods) Hold(m);
        foreach (var k in keys)
        {
            Tap(k);
            await Delay(ct, 5).ConfigureAwait(false);
        }
        for (int i = mods.Count - 1; i >= 0; i--) Release(mods[i]);
    }

    private async Task TypeStringAsync(string text, IProgress<string>? log, CancellationToken ct)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            short res = VkKeyScanW(ch);                                // active keyboard layout -> VK + shift state
            if (res == -1) { log?.Report($"> char '{ch}' not on current keyboard layout - skipped"); continue; }
            ushort vk = (ushort)(res & 0xFF);
            byte shift = (byte)((res >> 8) & 0xFF);                    // 1=shift 2=ctrl 4=alt
            var mods = new List<ushort>();
            if ((shift & 1) != 0) mods.Add(0x10);                      // VK_SHIFT
            if ((shift & 2) != 0) mods.Add(0x11);                      // VK_CONTROL
            if ((shift & 4) != 0) mods.Add(0x12);                      // VK_MENU
            foreach (var m in mods) Hold(m);
            Tap(vk);
            for (int i = mods.Count - 1; i >= 0; i--) Release(mods[i]);
            await Delay(ct, 8).ConfigureAwait(false);                  // matches the ParseAsync estimate (8ms/char)
        }
    }

    private static bool IsModifier(string name) => name.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" or "LCTRL" or "RCTRL" => true,
        "SHIFT" or "LSHIFT" or "RSHIFT" => true,
        "ALT" or "LALT" or "RALT" => true,
        "GUI" or "WINDOWS" or "WIN" or "CMD" or "COMMAND" or "META" or "LGUI" or "RGUI" or "LWIN" or "RWIN" => true,
        _ => false
    };

    /// <summary>Key name -> Windows virtual-key code (real VK constants).</summary>
    private static ushort? Vk(string name)
    {
        switch (name.ToUpperInvariant())
        {
            case "ENTER": case "RETURN": return 0x0D;
            case "TAB": return 0x09;
            case "ESC": case "ESCAPE": return 0x1B;
            case "SPACE": case "SPACEBAR": return 0x20;
            case "BACKSPACE": case "BKSP": return 0x08;
            case "DELETE": case "DEL": return 0x2E;
            case "UP": case "UPARROW": return 0x26;
            case "DOWN": case "DOWNARROW": return 0x28;
            case "LEFT": case "LEFTARROW": return 0x25;
            case "RIGHT": case "RIGHTARROW": return 0x27;
            case "HOME": return 0x24;
            case "END": return 0x23;
            case "PAGEUP": return 0x21;
            case "PAGEDOWN": return 0x22;
            case "INSERT": return 0x2D;
            case "PRINTSCREEN": case "SYSRQ": return 0x2C;
            case "CAPSLOCK": return 0x14;
            case "GUI": case "WINDOWS": case "WIN": case "LGUI": case "LWIN": return 0x5B;
            case "RGUI": case "RWIN": return 0x5C;
            case "CTRL": case "CONTROL": case "LCTRL": return 0x11;
            case "RCTRL": return 0xA3;
            case "SHIFT": case "LSHIFT": return 0x10;
            case "RSHIFT": return 0xA1;
            case "ALT": case "LALT": return 0x12;
            case "RALT": return 0xA5;
            case "MENU": case "APP": return 0x5D;
        }
        if (name.Length == 2 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name[1..], out int f) && f >= 1 && f <= 12)
            return (ushort)(0x70 + f - 1);                             // F1..F12 = 0x70..0x7B
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') return (ushort)c;
            if (c >= '0' && c <= '9') return (ushort)c;
        }
        return null;
    }

    // ============================ SendInput ============================

    private const uint INPUT_KEYBOARD = 1u;
    private const uint KEYEVENTF_KEYUP = 0x0002u;
    private const uint MAPVK_VK_TO_VSC = 0u;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", EntryPoint = "VkKeyScanW", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    private void SendKey(ushort vk, bool up)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_KEYBOARD;
        input[0].U.ki.wVk = vk;
        input[0].U.ki.wScan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC); // real scancode alongside the VK
        input[0].U.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0u;
        if (SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) != 1)
            _log.Warn("hid", $"SendInput failed (vk=0x{vk:X2}, up={up}, win32={Marshal.GetLastWin32Error()})");
    }

    private void Hold(ushort vk)
    {
        lock (_heldLock) { if (!_heldKeys.Contains(vk)) _heldKeys.Add(vk); }
        SendKey(vk, false);
    }

    private void Release(ushort vk)
    {
        lock (_heldLock) { _heldKeys.Remove(vk); }
        SendKey(vk, true);
    }

    private void Tap(ushort vk)
    {
        SendKey(vk, false);
        Thread.Sleep(2);                                               // tiny gap so hosts register both edges
        SendKey(vk, true);
    }

    /// <summary>Safety net: releases any modifier left held (e.g. after Stop).</summary>
    private void ReleaseAllHeld()
    {
        ushort[] held;
        lock (_heldLock) { held = _heldKeys.ToArray(); _heldKeys.Clear(); }
        foreach (var vk in held) SendKey(vk, true);
    }

    private static Task Delay(CancellationToken ct, int ms) => ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;

    private static async Task<string[]> ReadLinesAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
