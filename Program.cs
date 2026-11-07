using System.Runtime.InteropServices;

ApplicationConfiguration.Initialize();
Application.Run(new ClickerForm());

class ClickerForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int HotkeyId = 1, ShowHotkeyId = 2, WM_HOTKEY = 0x0312, WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const uint MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000, VK_F12 = 0x7B;

    readonly CheckBox chkHold = new() { AutoSize = true };
    readonly CheckBox chkAuto = new() { Text = "Start with Windows (minimized to tray)", AutoSize = true };
    readonly NotifyIcon tray = new() { Icon = AppIcon(SystemInformation.SmallIconSize), Text = "PAX Auto Clicker (Shift+F12 to open)" };

    static Icon AppIcon(Size size)
    {
        using var s = typeof(ClickerForm).Assembly.GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s, size) : SystemIcons.Application;
    }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", AppKey = @"Software\PaxAutoClicker", RunName = "PaxAutoClicker";
    const string LegacyAppKey = @"Software\AutoClicker", LegacyRunName = "AutoClicker"; // names before the PAX rename
    readonly CheckBox chkRing = new() { Text = "Show circle at each click", AutoSize = true, Checked = true };
    readonly ClickRing ring = new();
    volatile bool ringOn = true;
    // The ring overlay can't draw over exclusive-fullscreen games, so clicks can also be heard as a soft tick.
    readonly CheckBox chkTick = new() { Text = "Tick sound at each click (works over fullscreen games)", AutoSize = true };
    volatile bool tickOn;
    long lastTickMs;
    readonly System.Diagnostics.Stopwatch tickClock = System.Diagnostics.Stopwatch.StartNew();
    bool autostartLaunch, muteNextToggle;
    HookProc? hookProc;         // kept as a field so the GC doesn't collect the delegate
    IntPtr hook;
    volatile bool physLeftDown;  // the real button state, ignoring clicks we inject
    CancellationTokenSource? holdCts;
    HookProc? kbProc;
    IntPtr kbHook;

    // Bindable keys: holdVk toggles hold mode, toggleVk starts/stops the clicker, modVk is held with left click to spam.
    enum KeyCapture { None, HoldToggle, StartStop, HoldMod }
    int holdVk = 0x22;           // Page Down by default
    int toggleVk = 0x75;         // F6
    volatile int modVk = 0x10;   // Shift
    KeyCapture capturing;
    bool holdKeyDown;
    readonly Button btnHoldKey = new() { Width = 110 };
    readonly Button btnToggleKey = new() { Width = 110 };
    readonly Button btnModKey = new() { Width = 110 };

    readonly NumericUpDown nCps = new() { Minimum = 1, Maximum = 500, Value = 20, Width = 70 };
    readonly NumericUpDown nCount = new() { Minimum = 0, Maximum = 10_000_000, Value = 0, Width = 90 };
    readonly NumericUpDown nStartDelay = new() { Minimum = 0, Maximum = 60, Value = 3, Width = 50 };
    readonly ComboBox cbButton = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly TrackBar tbVolume = new() { Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10, Width = 120, AutoSize = false, Height = 28 };
    readonly Label lblVolume = new() { AutoSize = true, ForeColor = Color.Gray };
    readonly Button btn = new() { Text = "Start", Width = 120, Height = 36 };
    readonly Label status = new() { AutoSize = true, Text = "Stopped" };
    CancellationTokenSource? cts;
    long clicks;

    public ClickerForm()
    {
        Text = "PAX Auto Clicker";
        Icon = AppIcon(new Size(32, 32));
        ClientSize = new Size(380, 250);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9);
        TopMost = true;

        cbButton.Items.AddRange(new object[] { "Left", "Right", "Middle" }); cbButton.SelectedIndex = 0;

        int y = 14;
        void Row(string label, Control c, string? hint = null)
        {
            Controls.Add(new Label { Text = label, Left = 14, Top = y + 4, AutoSize = true });
            c.Left = 170; c.Top = y; Controls.Add(c);
            if (hint != null) Controls.Add(new Label { Text = hint, Left = c.Right + 6, Top = y + 4, AutoSize = true, ForeColor = Color.Gray });
            y += 32;
        }
        Row("Clicks per second", nCps);
        Row("Mouse button", cbButton);
        Row("Stop after N clicks", nCount, "0 = forever");
        Row("Start delay (sec)", nStartDelay);
        Row("Start/stop hotkey", btnToggleKey, "click to change");
        Row("Hold mode on/off key", btnHoldKey, "click to change");
        Row("Hold with left click", btnModKey, "click to change");
        Row("Sound volume", tbVolume);
        lblVolume.Left = tbVolume.Right + 6; lblVolume.Top = tbVolume.Top + 4; Controls.Add(lblVolume);

        chkHold.Left = 14; chkHold.Top = y; Controls.Add(chkHold);
        chkHold.CheckedChanged += (_, _) =>
        {
            if (chkHold.Checked) StartHoldMode(); else StopHoldMode();
            // Only the silent auto-enable at Windows startup skips the sound; tray/hidden toggles still play it.
            if (muteNextToggle) muteNextToggle = false; else PlayToggleSound(chkHold.Checked);
        };
        y += 24;
        chkRing.Left = 14; chkRing.Top = y; Controls.Add(chkRing);
        chkRing.CheckedChanged += (_, _) => ringOn = chkRing.Checked;
        y += 24;
        chkTick.Left = 14; chkTick.Top = y; Controls.Add(chkTick);
        chkTick.CheckedChanged += (_, _) => tickOn = chkTick.Checked;
        y += 24;
        chkAuto.Left = 14; chkAuto.Top = y; Controls.Add(chkAuto);
        y += 28;
        ClientSize = new Size(380, y + 60);

        LoadSettings();
        UpdateKeyTexts();
        SetVolume(tbVolume.Value);
        tbVolume.ValueChanged += (_, _) => SetVolume(tbVolume.Value);
        tbVolume.MouseUp += (_, _) => PlayToggleSound(true); // preview at the new level
        btnToggleKey.Click += (_, _) => BeginCapture(KeyCapture.StartStop, btnToggleKey);
        btnHoldKey.Click += (_, _) => BeginCapture(KeyCapture.HoldToggle, btnHoldKey);
        btnModKey.Click += (_, _) => BeginCapture(KeyCapture.HoldMod, btnModKey);
        kbProc = KeyboardProc;
        kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, GetModuleHandle(null), 0);
        chkAuto.CheckedChanged += (_, _) => SetAutostart(chkAuto.Checked);
        autostartLaunch = Environment.GetCommandLineArgs().Contains("--autostart");

        // tray: minimizing hides the window; double-click, menu or Shift+F12 restores it
        tray.Visible = true;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Show", null, (_, _) => RestoreFromTray());
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => { tray.Visible = false; Close(); });
        tray.DoubleClick += (_, _) => RestoreFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        Shown += (_, _) =>
        {
            if (autostartLaunch) { muteNextToggle = true; chkHold.Checked = true; WindowState = FormWindowState.Minimized; Hide(); }
        };

        btn.Left = 14; btn.Top = y + 6; btn.Click += (_, _) => Toggle();
        status.Left = 150; status.Top = y + 15;
        Controls.Add(btn); Controls.Add(status);

        HandleCreated += (_, _) =>
        {
            RegisterKey();
            if (!RegisterHotKey(Handle, ShowHotkeyId, MOD_SHIFT | MOD_NOREPEAT, VK_F12)) status.Text = "Shift+F12 is taken by another app";
        };
        FormClosing += (_, _) => { if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook); ring.Close(); SaveSettings(); tray.Visible = false; cts?.Cancel(); StopHoldMode(); UnregisterHotKey(Handle, HotkeyId); UnregisterHotKey(Handle, ShowHotkeyId); };
    }

    void RestoreFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    void SetAutostart(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --autostart");
            else k.DeleteValue(RunName, false);
        }
        catch (Exception e) { status.Text = "Autostart failed: " + e.Message; }
    }

    void LoadSettings()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppKey) ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LegacyAppKey);
            if (k != null)
            {
                nCps.Value = Math.Clamp(Convert.ToDecimal(k.GetValue("Cps", 20)), nCps.Minimum, nCps.Maximum);
                cbButton.SelectedIndex = Math.Clamp(Convert.ToInt32(k.GetValue("Button", 0)), 0, 2);
                // older versions stored the start/stop key as an F6..F10 dropdown index
                int legacy = 0x75 + Math.Clamp(Convert.ToInt32(k.GetValue("Hotkey", 0)), 0, 4);
                toggleVk = Math.Clamp(Convert.ToInt32(k.GetValue("ToggleKey", legacy)), 1, 254);
                holdVk = Math.Clamp(Convert.ToInt32(k.GetValue("HoldKey", 0x22)), 1, 254);
                modVk = Math.Clamp(Convert.ToInt32(k.GetValue("HoldMod", 0x10)), 1, 254);
                tbVolume.Value = Math.Clamp(Convert.ToInt32(k.GetValue("Volume", 70)), 0, 100);
                chkTick.Checked = tickOn = Convert.ToInt32(k.GetValue("Tick", 0)) != 0;
                chkRing.Checked = ringOn = Convert.ToInt32(k.GetValue("Ring", 1)) != 0;
            }
            using var r = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (r?.GetValue(LegacyRunName) != null)
            {
                // the old entry points at AutoClicker.exe, which no longer exists after the rename
                r.DeleteValue(LegacyRunName, false);
                r.SetValue(RunName, $"\"{Environment.ProcessPath}\" --autostart");
            }
            chkAuto.Checked = r?.GetValue(RunName) != null;
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AppKey);
            k.SetValue("Cps", (int)nCps.Value);
            k.SetValue("Button", cbButton.SelectedIndex);
            k.SetValue("ToggleKey", toggleVk);
            k.SetValue("HoldKey", holdVk);
            k.SetValue("HoldMod", modVk);
            k.SetValue("Volume", tbVolume.Value);
            k.SetValue("Tick", tickOn ? 1 : 0);
            k.SetValue("Ring", ringOn ? 1 : 0);
        }
        catch { }
    }

    static string KeyName(int vk) => (Keys)vk switch
    {
        Keys.Next => "Page Down", Keys.Prior => "Page Up", Keys.Back => "Backspace", Keys.Return => "Enter",
        Keys.ShiftKey => "Shift", Keys.ControlKey => "Ctrl", Keys.Menu => "Alt",
        var k => k.ToString(),
    };

    // Soft bell chimes built in memory: rising C-E-G = enabled, falling G-E-C (an octave lower) = disabled.
    // Played through the normal audio mixer (not the legacy Beep driver) and asynchronously.
    static readonly double[] onChime = MakeChime(1046.5, 1318.5, 1568.0);
    static readonly double[] offChime = MakeChime(784.0, 659.3, 523.25);
    static readonly double[] tickClip = MakeTick();
    System.Media.SoundPlayer? onSound, offSound, tickSound;

    // Called from the click loops; capped at ~25 ticks/sec so high click rates stay a crisp rattle, not a buzz.
    void ClickFeedback()
    {
        if (ringOn) ring.Pulse();
        if (!tickOn) return;
        long now = tickClock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref lastTickMs) < 40) return;
        Interlocked.Exchange(ref lastTickMs, now);
        try { tickSound?.Play(); } catch { }
    }

    static double[] MakeTick()
    {
        const int rate = 44100, len = rate * 18 / 1000;
        var rnd = new Random(7);
        var mix = new double[len];
        for (int i = 0; i < len; i++)
        {
            double t = (double)i / rate, env = Math.Exp(-t * 320);
            mix[i] = env * (0.7 * Math.Sin(2 * Math.PI * 2400 * t) + 0.3 * (rnd.NextDouble() * 2 - 1));
        }
        return mix;
    }

    void PlayToggleSound(bool on)
    {
        try { (on ? onSound : offSound)?.Play(); } catch { }
    }

    void SetVolume(int percent)
    {
        lblVolume.Text = percent + "%";
        onSound?.Dispose(); offSound?.Dispose(); tickSound?.Dispose();
        if (percent == 0) { onSound = offSound = tickSound = null; return; }
        double gain = 0.7 * Math.Pow(percent / 100.0, 2); // squared so the slider feels even to the ear
        onSound = ToPlayer(onChime, gain);
        offSound = ToPlayer(offChime, gain);
        tickSound = ToPlayer(tickClip, gain * 0.6);
    }

    static double[] MakeChime(params double[] freqs)
    {
        const int rate = 44100, stepMs = 70, tailMs = 260;
        int stepLen = rate * stepMs / 1000, noteLen = rate * tailMs / 1000;
        var mix = new double[stepLen * (freqs.Length - 1) + noteLen];
        for (int n = 0; n < freqs.Length; n++)
            for (int i = 0; i < noteLen; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Exp(-t * 14); // quick attack, bell-like decay
                double s = Math.Sin(2 * Math.PI * freqs[n] * t) + 0.25 * Math.Sin(4 * Math.PI * freqs[n] * t);
                mix[n * stepLen + i] += s * env;
            }
        return mix;
    }

    static System.Media.SoundPlayer ToPlayer(double[] mix, double gain)
    {
        const int rate = 44100;
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataLen = mix.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(dataLen);
            foreach (var v in mix) w.Write((short)(Math.Clamp(v * gain, -1, 1) * short.MaxValue));
        }
        ms.Position = 0;
        var p = new System.Media.SoundPlayer(ms);
        p.Load();
        return p;
    }

    void UpdateKeyTexts()
    {
        btnHoldKey.Text = KeyName(holdVk);
        btnToggleKey.Text = KeyName(toggleVk);
        btnModKey.Text = KeyName(modVk);
        chkHold.Text = $"Hold {KeyName(modVk)} + Left click to spam left click";
    }

    // A bare key binding shouldn't fire for combos meant for other apps (e.g. Ctrl+Page Down).
    static bool ModifierHeld() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);

    void BeginCapture(KeyCapture what, Button b) { capturing = what; b.Text = "Press a key…"; }

    // Low-level keyboard hook (does not swallow keys, so Page Down still works elsewhere).
    IntPtr KeyboardProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(l, 0), msg = (int)w;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN, up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (down && capturing != KeyCapture.None)
            {
                // the hook reports left/right variants; the generic code matches either side
                vk = vk switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => vk };
                bool modifier = vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
                if (capturing == KeyCapture.HoldMod || !modifier)
                {
                    var target = capturing;
                    capturing = KeyCapture.None;
                    if (vk != 0x1B) // Esc cancels
                    {
                        if (target == KeyCapture.HoldToggle) { holdVk = vk; holdKeyDown = true; }
                        else if (target == KeyCapture.StartStop) { toggleVk = vk; RegisterKey(); }
                        else modVk = vk;
                    }
                    UpdateKeyTexts();
                    return (IntPtr)1; // swallow the key so binding it doesn't also trigger it
                }
            }
            else if (down && vk == holdVk && !holdKeyDown && !ModifierHeld())
            {
                holdKeyDown = true; // ignore auto-repeat while held
                BeginInvoke(() =>
                {
                    chkHold.Checked = !chkHold.Checked;
                    if (!chkHold.Checked) status.Text = "Hold mode off";
                });
            }
            else if (up && vk == holdVk) holdKeyDown = false;
        }
        return CallNextHookEx(kbHook, code, w, l);
    }

    void StartHoldMode()
    {
        hookProc = (code, w, l) =>
        {
            if (code >= 0)
            {
                // MSLLHOOKSTRUCT.flags is at offset 12; bit 0 = injected (our own clicks)
                bool injected = (Marshal.ReadInt32(l, 12) & 1) != 0;
                if (!injected)
                {
                    if ((int)w == WM_LBUTTONDOWN) physLeftDown = true;
                    else if ((int)w == WM_LBUTTONUP) physLeftDown = false;
                }
            }
            return CallNextHookEx(hook, code, w, l);
        };
        hook = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
        holdCts = new CancellationTokenSource();
        var token = holdCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (physLeftDown && (GetAsyncKeyState(modVk) & 0x8000) != 0)
                {
                    Click(0x0002, 0x0004);
                    ClickFeedback();
                    await Task.Delay(TimeSpan.FromSeconds(1.0 / (double)nCps.Value));
                }
                else await Task.Delay(5);
            }
        });
        SetStatus($"Hold mode on: hold {KeyName(modVk)} + left click");
    }

    void StopHoldMode()
    {
        holdCts?.Cancel(); holdCts = null;
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        physLeftDown = false;
    }

    void RegisterKey()
    {
        if (!IsHandleCreated) return;
        UnregisterHotKey(Handle, HotkeyId);
        if (!RegisterHotKey(Handle, HotkeyId, MOD_NOREPEAT, (uint)toggleVk)) status.Text = $"{KeyName(toggleVk)} is taken by another app";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == (IntPtr)HotkeyId) Toggle();
        else if (m.Msg == WM_HOTKEY && m.WParam == (IntPtr)ShowHotkeyId)
        {
            if (Visible && WindowState != FormWindowState.Minimized) Hide(); else RestoreFromTray();
        }
        base.WndProc(ref m);
    }

    void Toggle()
    {
        if (cts != null) { cts.Cancel(); return; }
        cts = new CancellationTokenSource();
        var token = cts.Token;
        double cps = (double)nCps.Value;
        long max = (long)nCount.Value;
        int delay = (int)nStartDelay.Value;
        var (down, up) = cbButton.SelectedIndex switch { 1 => (0x0008u, 0x0010u), 2 => (0x0020u, 0x0040u), _ => (0x0002u, 0x0004u) };
        btn.Text = "Stop"; clicks = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                for (int s = delay; s > 0 && !token.IsCancellationRequested; s--)
                {
                    SetStatus($"Starting in {s}…");
                    await Task.Delay(1000, token);
                }
                var interval = TimeSpan.FromSeconds(1.0 / cps);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var next = TimeSpan.Zero;
                while (!token.IsCancellationRequested && (max == 0 || clicks < max))
                {
                    Click(down, up);
                    ClickFeedback();
                    clicks++;
                    if (clicks % 10 == 0) SetStatus($"Clicking… {clicks} clicks");
                    next += interval;
                    var wait = next - sw.Elapsed;
                    if (wait > TimeSpan.FromMilliseconds(2)) await Task.Delay(wait - TimeSpan.FromMilliseconds(1), token);
                    else if (wait > TimeSpan.Zero) Thread.SpinWait(200);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                BeginInvoke(() => { btn.Text = "Start"; status.Text = $"Stopped ({clicks} clicks)"; cts?.Dispose(); cts = null; });
            }
        });
    }

    void SetStatus(string t) => BeginInvoke(() => status.Text = t);

    static void Click(uint down, uint up)
    {
        var inputs = new INPUT[]
        {
            new() { type = 0, mi = new MOUSEINPUT { flags = down } },
            new() { type = 0, mi = new MOUSEINPUT { flags = up } },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }
}

// Transparent, click-through overlay that flashes a fading ring where the last click landed.
class ClickRing : Form
{
    const int Size_ = 72, DurationMs = 260;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    volatile bool pending;
    Point pendingPos;
    long startMs = -DurationMs;

    public ClickRing()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        ClientSize = new Size(Size_, Size_);
        DoubleBuffered = true;
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Show();
        Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered | transparent (click-through) | toolwindow (no alt-tab) | noactivate
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000;
            return cp;
        }
    }

    // Safe to call from any thread: only records the latest click position.
    public void Pulse() { pendingPos = Cursor.Position; pending = true; }

    void Tick()
    {
        if (pending)
        {
            pending = false;
            Location = new Point(pendingPos.X - Size_ / 2, pendingPos.Y - Size_ / 2);
            startMs = clock.ElapsedMilliseconds;
            if (!Visible) Show();
        }
        long age = clock.ElapsedMilliseconds - startMs;
        if (age >= DurationMs) { if (Visible) Hide(); return; }
        Opacity = 1.0 - (double)age / DurationMs;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        double t = Math.Clamp((double)(clock.ElapsedMilliseconds - startMs) / DurationMs, 0, 1);
        float r = 8 + (float)t * 24;
        using var pen = new Pen(Color.Lime, 3);
        e.Graphics.DrawEllipse(pen, Size_ / 2f - r, Size_ / 2f - r, r * 2, r * 2);
    }
}
