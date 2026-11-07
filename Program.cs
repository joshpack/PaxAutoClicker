using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

App.Run(args);

static class App
{
    public const string Id = "PaxAutoClicker";

    public static void Run(string[] args)
    {
        // A second launch hands over to the running copy (which shows its window) and exits.
        using var single = SingleInstance.Claim(@"Local\PaxAutoClicker.Instance", @"Local\PaxAutoClicker.Show", signalIfRunning: !args.Contains("--autostart"));
        if (single == null) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new ClickerForm(single.ShowRequest));
    }
}

// One copy per user session: a named mutex says who runs, a named event asks the running copy to show its window.
sealed class SingleInstance : IDisposable
{
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    readonly Mutex mutex;
    public EventWaitHandle ShowRequest { get; }

    SingleInstance(Mutex m, EventWaitHandle e) { mutex = m; ShowRequest = e; }

    // Returns null when another copy already runs (after asking it to show itself if signalIfRunning).
    public static SingleInstance? Claim(string mutexName, string eventName, bool signalIfRunning)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var m = new Mutex(true, mutexName, out bool first);
        if (first) return new SingleInstance(m, ev);
        if (signalIfRunning)
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: let the running copy come to the front
            ev.Set();
        }
        m.Dispose(); ev.Dispose();
        return null;
    }

    public void Dispose()
    {
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose(); ShowRequest.Dispose();
    }
}

// PAX Toolkit link: %APPDATA%\PAX\toolkit.json -> apps.<id>.trayIcon (default true). PAX_SHARED_DIR overrides the folder.
static class PaxLink
{
    public static string SharedDir =>
        Environment.GetEnvironmentVariable("PAX_SHARED_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PAX");

    public static string FilePath => Path.Combine(SharedDir, "toolkit.json");

    public static bool TrayIconVisible(string appId) =>
        Read()?["apps"]?[appId]?["trayIcon"] is JsonValue v && v.TryGetValue(out bool on) ? on : true;

    // Changes only this app's entry; other apps' entries and other keys are kept. Written via a temp file + move.
    public static void SetTrayIconVisible(string appId, bool visible)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var root = Read() as JsonObject ?? new JsonObject { ["schema"] = 1 };
                var apps = root["apps"] as JsonObject ?? new JsonObject();
                root["apps"] = apps;
                var entry = apps[appId] as JsonObject ?? new JsonObject();
                apps[appId] = entry;
                entry["trayIcon"] = visible;
                Directory.CreateDirectory(SharedDir);
                var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }

    // Calls changed (on a worker thread) 250 ms after the last change to toolkit.json.
    public static IDisposable? Watch(Action changed)
    {
        try
        {
            Directory.CreateDirectory(SharedDir);
            var w = new FileSystemWatcher(SharedDir, "toolkit.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
            System.Threading.Timer? debounce = null;
            void Fire(object? _, FileSystemEventArgs __)
            {
                debounce?.Dispose();
                debounce = new System.Threading.Timer(_ => changed(), null, 250, Timeout.Infinite);
            }
            w.Changed += Fire;
            w.Created += Fire;
            w.Deleted += Fire;
            w.Renamed += (s, e) => Fire(s, e);
            w.EnableRaisingEvents = true;
            return w;
        }
        catch { return null; }
    }

    static JsonNode? Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) : null; }
            catch (IOException) { Thread.Sleep(50); }
            catch { return null; } // damaged file: treat as empty
        }
        return null;
    }
}

static class PaxPlacement
{
    // A saved spot counts only if a real part of the title bar lands in some screen's working area.
    public static bool IsVisible(Rectangle window, int barHeight, IEnumerable<Rectangle> workAreas)
    {
        var bar = new Rectangle(window.X, window.Y, window.Width, barHeight);
        return workAreas.Any(a => { var i = Rectangle.Intersect(a, bar); return i.Width >= Math.Min(80, window.Width) && i.Height >= barHeight / 2; });
    }

    public static bool IsVisible(Rectangle window, int barHeight) =>
        IsVisible(window, barHeight, Screen.AllScreens.Select(s => s.WorkingArea));
}

// The PAX title bar: 38 px #0A0D12 with a 1 px #1B2030 line under it, the app icon (18 px) and name on the left,
// minimize and close (46x38) at the right. Empty bar area drags the window.
sealed class PaxTitleBar : Control
{
    static readonly Color Back = ColorTranslator.FromHtml("#0A0D12"), Line = ColorTranslator.FromHtml("#1B2030"),
        Fore = ColorTranslator.FromHtml("#C9CED9"), Hover = ColorTranslator.FromHtml("#1F2533"), CloseHover = ColorTranslator.FromHtml("#E81123");
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

    readonly Bitmap? icon;
    readonly ToolTip tip = new();
    int hot = -1, pressed = -1; // 0 = minimize, 1 = close
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CloseTip { get; set; } = "Close";
    public Action? CloseAction; // replaces closing the form when the X is clicked

    public PaxTitleBar(string title, Bitmap? icon)
    {
        Text = title;
        this.icon = icon;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Dock = DockStyle.Top;
        BackColor = Back;
        Height = BarHeight + 1;
    }

    float Scale_ => DeviceDpi / 96f;
    int Px(double v) => (int)Math.Round(v * Scale_);
    public int BarHeight => Px(38);

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = BarHeight + 1; Invalidate(); }

    Rectangle ButtonRect(int i) { int w = Px(46); return new Rectangle(Width - w * (2 - i), 0, w, BarHeight); }
    int HitButton(Point p) => ButtonRect(1).Contains(p) ? 1 : ButtonRect(0).Contains(p) ? 0 : -1;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Back)) g.FillRectangle(bg, ClientRectangle);
        if (hot >= 0) using (var b = new SolidBrush(hot == 1 ? CloseHover : Hover)) g.FillRectangle(b, ButtonRect(hot));
        using (var line = new SolidBrush(Line)) g.FillRectangle(line, 0, BarHeight, Width, Height - BarHeight);

        int x = Px(12);
        if (icon != null)
        {
            int s = Px(18);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(icon, new Rectangle(x, (BarHeight - s) / 2, s, s));
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default;
            x += s + Px(9);
        }
        using (var font = new Font("Segoe UI Semibold", 12.5f * Scale_, FontStyle.Regular, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, Text, font, new Rectangle(x, 0, Math.Max(0, ButtonRect(0).Left - x), BarHeight), Fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // glyphs: 10x10 (at 96 dpi), 1 px strokes, centred in each button
        using var pen = new Pen(Fore, Math.Max(1f, Scale_));
        int gs = Px(10), gy = (BarHeight - gs) / 2;
        var m = ButtonRect(0);
        int mx = m.Left + (m.Width - gs) / 2, my = gy + Px(5);
        g.DrawLine(pen, mx, my, mx + gs, my);
        var c = ButtonRect(1);
        int cx = c.Left + (c.Width - gs) / 2;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawLine(pen, cx, gy, cx + gs, gy + gs);
        g.DrawLine(pen, cx + gs, gy, cx, gy + gs);
    }

    void SetHot(int h)
    {
        if (h == hot) return;
        hot = h;
        tip.SetToolTip(this, h == 0 ? "Minimize" : h == 1 ? CloseTip : null);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHot(HitButton(e.Location)); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHot(-1); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressed = HitButton(e.Location);
        if (pressed < 0 && FindForm() is { } f)
        {
            ReleaseCapture();
            SendMessage(f.Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int p = pressed;
        pressed = -1;
        if (e.Button != MouseButtons.Left || p < 0 || HitButton(e.Location) != p || FindForm() is not { } f) return;
        if (p == 0) f.WindowState = FormWindowState.Minimized; else if (CloseAction != null) CloseAction(); else f.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { icon?.Dispose(); tip.Dispose(); }
        base.Dispose(disposing);
    }
}

// PAX dark theme colours and helpers for the stock WinForms controls.
static class PaxTheme
{
    public static readonly Color Back = Hex("#0E1015"), Text = Hex("#E8EAF0"), Muted = Hex("#8A92A6"),
        Surface = Hex("#161922"), Border = Hex("#272C39"), Raised = Hex("#1D212C"), RaisedHover = Hex("#252A37"),
        HoverBorder = Hex("#2B7C9A"), Accent = Hex("#19F58C"), AccentHover = Hex("#5CF9AE"), OnAccent = Hex("#04140C"),
        Danger = Hex("#F2666A"), DangerHover = Hex("#F58A8D"), OnDanger = Hex("#1C0607"), Tick = Hex("#3A4152");

    static Color Hex(string s) => ColorTranslator.FromHtml(s);

    public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Raised secondary button (#1D212C, 1 px #272C39 border that turns #2B7C9A on hover).
    public static void RaisedButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.BackColor = Raised;
        b.ForeColor = Text;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = RaisedHover;
        b.FlatAppearance.MouseDownBackColor = Surface;
        b.MouseEnter += (_, _) => b.FlatAppearance.BorderColor = HoverBorder;
        b.MouseLeave += (_, _) => b.FlatAppearance.BorderColor = Border;
    }

    // Primary button: #19F58C with dark text; turns #F2666A while its text is "Stop" (the clicker is running).
    public static void PrimaryButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderSize = 0;
        b.Font = new Font(b.Font, FontStyle.Bold);
        void Style()
        {
            bool stop = b.Text == "Stop";
            b.BackColor = stop ? Danger : Accent;
            b.ForeColor = stop ? OnDanger : OnAccent;
            b.FlatAppearance.MouseOverBackColor = stop ? DangerHover : AccentHover;
            b.FlatAppearance.MouseDownBackColor = stop ? Danger : Accent;
        }
        Style();
        b.TextChanged += (_, _) => Style();
    }

    // Dark NumericUpDown: dark edit, 1 px border, and its spin buttons repainted dark.
    public static void Numeric(PaxNumericUpDown n)
    {
        n.BorderStyle = BorderStyle.FixedSingle;
        n.BackColor = Surface;
        n.ForeColor = Text;
        n.Enter += (_, _) => n.Invalidate();
        n.Leave += (_, _) => n.Invalidate();
        var spin = n.Controls.Cast<Control>().FirstOrDefault(c => c is not TextBox);
        if (spin == null) return;
        int hot = -1; bool pressed = false;
        spin.MouseMove += (_, e) => { int h = e.Y < spin.Height / 2 ? 0 : 1; if (h != hot) { hot = h; spin.Invalidate(); } };
        spin.MouseLeave += (_, _) => { hot = -1; pressed = false; spin.Invalidate(); };
        spin.MouseDown += (_, _) => { pressed = true; spin.Invalidate(); };
        spin.MouseUp += (_, _) => { pressed = false; spin.Invalidate(); };
        spin.Paint += (_, e) =>
        {
            var g = e.Graphics;
            float s = spin.DeviceDpi / 96f;
            int w = spin.Width, h = spin.Height, half = h / 2;
            using (var bg = new SolidBrush(Raised)) g.FillRectangle(bg, 0, 0, w, h);
            if (hot >= 0)
                using (var b = new SolidBrush(pressed ? Surface : RaisedHover))
                    g.FillRectangle(b, 0, hot == 0 ? 0 : half, w, hot == 0 ? half : h - half);
            using (var line = new Pen(Border)) { g.DrawLine(line, 0, 0, 0, h); g.DrawLine(line, 0, half, w, half); }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float cx = w / 2f + 0.5f, a = 3.2f * s;
            for (int i = 0; i < 2; i++)
            {
                float cy = i == 0 ? half / 2f + 0.5f : half + (h - half) / 2f;
                var pts = i == 0
                    ? new[] { new PointF(cx - a, cy + a / 2), new PointF(cx + a, cy + a / 2), new PointF(cx, cy - a / 2) }
                    : new[] { new PointF(cx - a, cy - a / 2), new PointF(cx + a, cy - a / 2), new PointF(cx, cy + a / 2) };
                using var b = new SolidBrush(hot == i ? Text : Muted);
                g.FillPolygon(b, pts);
            }
        };
    }
}

// NumericUpDown with a 1 px #272C39 border (#2B7C9A while focused) and a little room before the number.
sealed class PaxNumericUpDown : NumericUpDown
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    const int EM_SETMARGINS = 0xD3, EC_LEFTMARGIN = 1;

    public PaxNumericUpDown() { PaxTheme.Numeric(this); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (Control c in Controls)
            if (c is TextBox t)
            {
                void Margin() => SendMessage(t.Handle, EM_SETMARGINS, EC_LEFTMARGIN, (IntPtr)(int)Math.Round(3 * DeviceDpi / 96f));
                if (t.IsHandleCreated) Margin(); else t.HandleCreated += (_, _) => Margin();
            }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(ContainsFocus ? PaxTheme.HoverBorder : PaxTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

// Check box drawn in the PAX look: dark box with a #272C39 border, #19F58C fill and dark tick when checked.
sealed class PaxCheckBox : CheckBox
{
    bool hover;

    public PaxCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = true;
    }

    float Scale_ => DeviceDpi / 96f;
    int BoxSize => (int)Math.Round(14 * Scale_);
    int Gap => (int)Math.Round(6 * Scale_);

    public override Size GetPreferredSize(Size proposed)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(BoxSize + Gap + text.Width + 2, Math.Max(base.GetPreferredSize(proposed).Height, BoxSize + 2));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = Scale_;
        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        int box = BoxSize;
        var r = new RectangleF(0.5f, (Height - box) / 2 + 0.5f, box - 1, box - 1);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = PaxTheme.Round(r, 3 * s))
        {
            using var fill = new SolidBrush(Checked ? (hover ? PaxTheme.AccentHover : PaxTheme.Accent) : PaxTheme.Surface);
            g.FillPath(fill, path);
            if (!Checked || (Focused && ShowFocusCues))
            {
                using var pen = new Pen(hover || (Focused && ShowFocusCues) ? PaxTheme.HoverBorder : PaxTheme.Border);
                g.DrawPath(pen, path);
            }
        }
        if (Checked)
        {
            using var tick = new Pen(PaxTheme.OnAccent, 2 * s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(tick, new[] { new PointF(r.X + 3.3f * s, r.Y + 6.8f * s), new PointF(r.X + 5.8f * s, r.Y + 9.3f * s), new PointF(r.X + 10.2f * s, r.Y + 4.2f * s) });
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(box + Gap, 0, Math.Max(0, Width - box - Gap), Height), Enabled ? ForeColor : PaxTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

// Drop-down list drawn dark: the closed box is painted here (also for WM_PRINT), the list items are owner-drawn.
sealed class PaxComboBox : ComboBox
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    const int WM_PAINT = 0x000F, WM_PRINT = 0x0317, WM_PRINTCLIENT = 0x0318;
    bool hover;

    public PaxComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = PaxTheme.Surface;
        ForeColor = PaxTheme.Text;
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = Font.Height + 2; } // same height as the stock control
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? PaxTheme.Raised : PaxTheme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
        if (sel) using (var a = new SolidBrush(PaxTheme.Accent)) e.Graphics.FillRectangle(a, e.Bounds.X, e.Bounds.Y, Math.Max(2, (int)Math.Round(2 * DeviceDpi / 96f)), e.Bounds.Height);
        var r = e.Bounds; r.X += (int)Math.Round(6 * DeviceDpi / 96f);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, sel ? PaxTheme.Text : PaxTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    void PaintBox(Graphics g)
    {
        float s = DeviceDpi / 96f;
        using (var bg = new SolidBrush(PaxTheme.Surface)) g.FillRectangle(bg, 0, 0, Width, Height);
        using (var pen = new Pen(hover || Focused || DroppedDown ? PaxTheme.HoverBorder : PaxTheme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        int arrowW = (int)Math.Round(20 * s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)Math.Round(5 * s), 0, Math.Max(0, Width - arrowW - (int)(5 * s)), Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float cx = Width - arrowW / 2f, cy = Height / 2f, a = 4 * s;
        using var chevron = new Pen(PaxTheme.Muted, 1.3f * s);
        g.DrawLines(chevron, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_PAINT)
        {
            var hdc = BeginPaint(Handle, out var ps);
            using (var g = Graphics.FromHdc(hdc)) PaintBox(g);
            EndPaint(Handle, ref ps);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg is WM_PRINT or WM_PRINTCLIENT && m.WParam != IntPtr.Zero)
        {
            using (var g = Graphics.FromHdc(m.WParam)) PaintBox(g);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}

// Track bar custom-drawn (NM_CUSTOMDRAW): #272C39 track filled #19F58C up to the thumb, a #19F58C thumb, muted ticks.
sealed class PaxTrackBar : TrackBar
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }
    [StructLayout(LayoutKind.Sequential)]
    struct NMCUSTOMDRAW { public NMHDR hdr; public int dwDrawStage; public IntPtr hdc; public RECT rc; public IntPtr dwItemSpec; public uint uItemState; public IntPtr lItemlParam; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT r);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    const int WM_REFLECT_NOTIFY = 0x204E, NM_CUSTOMDRAW = -12, CDDS_PREPAINT = 1, CDDS_ITEMPREPAINT = 0x10001;
    const int CDRF_NOTIFYITEMDRAW = 0x20, CDRF_SKIPDEFAULT = 4, TBCD_TICS = 1, TBCD_THUMB = 2, TBCD_CHANNEL = 3;
    const int TBM_GETTICPOS = 0x40F, TBM_GETNUMTICS = 0x410, TBM_GETTHUMBRECT = 0x419, TBM_GETCHANNELRECT = 0x41A;
    const uint CDIS_SELECTED = 1, CDIS_HOT = 0x40;

    public PaxTrackBar() { BackColor = PaxTheme.Back; }

    Rectangle GetRect(int msg) { var r = new RECT(); SendMessage(Handle, msg, IntPtr.Zero, ref r); return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_REFLECT_NOTIFY && Marshal.PtrToStructure<NMHDR>(m.LParam).code == NM_CUSTOMDRAW)
        {
            var cd = Marshal.PtrToStructure<NMCUSTOMDRAW>(m.LParam);
            if (cd.dwDrawStage == CDDS_PREPAINT) { m.Result = CDRF_NOTIFYITEMDRAW; return; }
            if (cd.dwDrawStage == CDDS_ITEMPREPAINT)
            {
                using var g = Graphics.FromHdc(cd.hdc);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float s = DeviceDpi / 96f;
                switch ((int)cd.dwItemSpec)
                {
                    case TBCD_CHANNEL:
                    {
                        var ch = GetRect(TBM_GETCHANNELRECT);
                        var th = GetRect(TBM_GETTHUMBRECT);
                        float h = 4 * s, y = (th.Top + th.Bottom) / 2f - h / 2;
                        var track = new RectangleF(ch.Left, y, ch.Width, h);
                        using (var p = PaxTheme.Round(track, h / 2)) using (var b = new SolidBrush(PaxTheme.Border)) g.FillPath(b, p);
                        var done = new RectangleF(ch.Left, y, Math.Max(h, (th.Left + th.Right) / 2f - ch.Left), h);
                        using (var p = PaxTheme.Round(done, h / 2)) using (var b = new SolidBrush(PaxTheme.Accent)) g.FillPath(b, p);
                        break;
                    }
                    case TBCD_THUMB:
                    {
                        var th = GetRect(TBM_GETTHUMBRECT);
                        var r = new RectangleF(th.Left + 0.5f, th.Top + 1.5f * s, th.Width - 1, th.Height - 3 * s);
                        bool hot = (cd.uItemState & (CDIS_HOT | CDIS_SELECTED)) != 0;
                        using var p = PaxTheme.Round(r, Math.Min(r.Width, r.Height) / 2);
                        using (var b = new SolidBrush(hot ? PaxTheme.AccentHover : PaxTheme.Accent)) g.FillPath(b, p);
                        using (var pen = new Pen(PaxTheme.OnAccent)) g.DrawPath(pen, p);
                        break;
                    }
                    case TBCD_TICS:
                    {
                        if (TickStyle == TickStyle.None) break;
                        var th = GetRect(TBM_GETTHUMBRECT);
                        int n = (int)SendMessage(Handle, TBM_GETNUMTICS, IntPtr.Zero, IntPtr.Zero);
                        var xs = new List<int>();
                        for (int i = 0; i < n - 2; i++) xs.Add((int)SendMessage(Handle, TBM_GETTICPOS, (IntPtr)i, IntPtr.Zero));
                        var ch = GetRect(TBM_GETCHANNELRECT);
                        int half = th.Width / 2;
                        xs.Add(ch.Left + half); xs.Add(ch.Right - half - 1); // the end ticks aren't returned by TBM_GETTICPOS
                        int top = th.Bottom + 1, len = Math.Max(2, (int)Math.Round(3 * s));
                        using var pen = new Pen(PaxTheme.Tick);
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                        foreach (var x in xs) g.DrawLine(pen, x, top, x, top + len);
                        break;
                    }
                }
                m.Result = CDRF_SKIPDEFAULT;
                return;
            }
        }
        base.WndProc(ref m);
    }
}

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

    readonly PaxCheckBox chkHold = new();
    readonly PaxCheckBox chkAuto = new() { Text = "Start with Windows (minimized to tray)" };
    readonly PaxCheckBox chkTray = new() { Text = "Show tray icon", Checked = true };
    readonly NotifyIcon tray = new() { Icon = AppIcon(SystemInformation.SmallIconSize), Text = "PAX Auto Clicker (Shift+F12 to open)" };
    readonly Panel body = new() { Dock = DockStyle.Fill };
    readonly PaxTitleBar bar = new("PAX Auto Clicker", AppIcon(new Size(64, 64)).ToBitmap()) { CloseTip = "Hide (use Exit to quit)" };
    readonly EventWaitHandle? showRequest;
    RegisteredWaitHandle? showWait;
    IDisposable? linkWatch;
    bool applyingLink;
    Point? savedPos;

    static Icon AppIcon(Size size)
    {
        using var s = typeof(ClickerForm).Assembly.GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s, size) : SystemIcons.Application;
    }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", AppKey = @"Software\PaxAutoClicker", RunName = "PaxAutoClicker";
    const string LegacyAppKey = @"Software\AutoClicker", LegacyRunName = "AutoClicker"; // names before the PAX rename
    readonly PaxCheckBox chkRing = new() { Text = "Show circle at each click", Checked = true };
    readonly ClickRing ring = new();
    volatile bool ringOn = true;
    // The ring overlay can't draw over exclusive-fullscreen games, so clicks can also be heard as a soft tick.
    readonly PaxCheckBox chkTick = new() { Text = "Tick sound at each click (works over fullscreen games)" };
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

    readonly PaxNumericUpDown nCps = new() { Minimum = 1, Maximum = 500, Value = 20, Width = 70 };
    readonly PaxNumericUpDown nCount = new() { Minimum = 0, Maximum = 10_000_000, Value = 0, Width = 90 };
    readonly PaxNumericUpDown nStartDelay = new() { Minimum = 0, Maximum = 60, Value = 3, Width = 50 };
    readonly PaxComboBox cbButton = new() { Width = 90 };
    readonly PaxTrackBar tbVolume = new() { Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10, Width = 120, AutoSize = false, Height = 28 };
    readonly Label lblVolume = new() { AutoSize = true, ForeColor = PaxTheme.Muted };
    readonly Button btn = new() { Text = "Start", Width = 120, Height = 36 };
    readonly Label status = new() { AutoSize = false, Text = "Stopped", TextAlign = ContentAlignment.MiddleLeft };
    readonly Button btnExit = new() { Text = "Exit", Width = 70, Height = 36 };
    CancellationTokenSource? cts;
    long clicks;

    public ClickerForm(EventWaitHandle? showRequest = null)
    {
        this.showRequest = showRequest;
        Text = "PAX Auto Clicker";
        Icon = AppIcon(new Size(32, 32));
        ClientSize = new Size(380, 250);
        FormBorderStyle = FormBorderStyle.None; // the PAX title bar replaces the system one
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9);
        TopMost = true;
        Controls.Add(body);
        Controls.Add(bar); // added last so it docks first, above the body
        // PAX dark theme: labels and check boxes take the body's colours; the inputs are styled below
        BackColor = PaxTheme.Back;
        body.BackColor = PaxTheme.Back;
        body.ForeColor = PaxTheme.Text;
        foreach (var b in new[] { btnToggleKey, btnHoldKey, btnModKey }) PaxTheme.RaisedButton(b);
        PaxTheme.PrimaryButton(btn);

        cbButton.Items.AddRange(new object[] { "Left", "Right", "Middle" }); cbButton.SelectedIndex = 0;

        int y = 14;
        void Row(string label, Control c, string? hint = null)
        {
            body.Controls.Add(new Label { Text = label, Left = 14, Top = y + 4, AutoSize = true });
            c.Left = 170; c.Top = y; body.Controls.Add(c);
            if (hint != null) body.Controls.Add(new Label { Text = hint, Left = c.Right + 6, Top = y + 4, AutoSize = true, ForeColor = PaxTheme.Muted });
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
        lblVolume.Left = tbVolume.Right + 6; lblVolume.Top = tbVolume.Top + 4; body.Controls.Add(lblVolume);

        chkHold.Left = 14; chkHold.Top = y; body.Controls.Add(chkHold);
        chkHold.CheckedChanged += (_, _) =>
        {
            if (chkHold.Checked) StartHoldMode(); else StopHoldMode();
            // Only the silent auto-enable at Windows startup skips the sound; tray/hidden toggles still play it.
            if (muteNextToggle) muteNextToggle = false; else PlayToggleSound(chkHold.Checked);
        };
        y += 24;
        chkRing.Left = 14; chkRing.Top = y; body.Controls.Add(chkRing);
        chkRing.CheckedChanged += (_, _) => ringOn = chkRing.Checked;
        y += 24;
        chkTick.Left = 14; chkTick.Top = y; body.Controls.Add(chkTick);
        chkTick.CheckedChanged += (_, _) => tickOn = chkTick.Checked;
        y += 24;
        chkAuto.Left = 14; chkAuto.Top = y; body.Controls.Add(chkAuto);
        y += 24;
        chkTray.Left = 14; chkTray.Top = y; body.Controls.Add(chkTray);
        y += 28;
        ClientSize = new Size(380, bar.Height + y + 60);

        LoadSettings();
        if (savedPos is { } pos && PaxPlacement.IsVisible(new Rectangle(pos, Size), bar.BarHeight))
        {
            StartPosition = FormStartPosition.Manual;
            Location = pos;
        }
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

        // tray: minimizing hides the window; double-click, menu or Shift+F12 restores it.
        // PAX Toolkit (toolkit.json) and the "Show tray icon" box both decide whether the icon shows.
        ApplyTrayLink(PaxLink.TrayIconVisible(App.Id));
        chkTray.CheckedChanged += (_, _) =>
        {
            if (applyingLink) return;
            tray.Visible = chkTray.Checked;
            PaxLink.SetTrayIconVisible(App.Id, chkTray.Checked);
        };
        linkWatch = PaxLink.Watch(() =>
        {
            bool on = PaxLink.TrayIconVisible(App.Id);
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => ApplyTrayLink(on)); } catch (InvalidOperationException) { }
        });
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Show", null, (_, _) => RestoreFromTray());
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitApp());
        tray.DoubleClick += (_, _) => RestoreFromTray();
        // without a tray icon, minimizing keeps the window on the taskbar instead of hiding it
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && tray.Visible) Hide(); };
        Shown += (_, _) =>
        {
            if (autostartLaunch) { muteNextToggle = true; chkHold.Checked = true; WindowState = FormWindowState.Minimized; Hide(); }
        };

        btn.Left = 14; btn.Top = y + 6; btn.Click += (_, _) => Toggle();
        btnExit.Left = 380 - 14 - btnExit.Width; btnExit.Top = y + 6; btnExit.Click += (_, _) => ExitApp();
        PaxTheme.RaisedButton(btnExit);
        status.Bounds = new Rectangle(btn.Right + 12, y + 6, btnExit.Left - btn.Right - 18, 36); // wraps to two lines if needed
        body.Controls.Add(btn); body.Controls.Add(status); body.Controls.Add(btnExit);

        HandleCreated += (_, _) =>
        {
            RegisterKey();
            if (!RegisterHotKey(Handle, ShowHotkeyId, MOD_SHIFT | MOD_NOREPEAT, VK_F12)) status.Text = "Shift+F12 is taken by another app";
            // another launch of the exe asks this copy to show its window
            if (showRequest != null && showWait == null)
                showWait = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) =>
                {
                    try { if (IsHandleCreated && !IsDisposed) BeginInvoke(RestoreFromTray); } catch (InvalidOperationException) { }
                }, null, Timeout.Infinite, false);
        };
        // X only hides; Alt+F4 and the taskbar's Close are caught in WndProc. A plain WM_CLOSE (PAX Toolkit
        // closing it to update, Windows shutdown) still quits normally so settings get saved.
        bar.CloseAction = HideWindow;
        FormClosing += (_, _) =>
        {
            showWait?.Unregister(null); linkWatch?.Dispose(); if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook); ring.Close(); SaveSettings(); tray.Visible = false; cts?.Cancel(); StopHoldMode(); UnregisterHotKey(Handle, HotkeyId); UnregisterHotKey(Handle, ShowHotkeyId);
        };
    }

    void ExitApp() { tray.Visible = false; Close(); }

    // with no tray icon a hidden window couldn't be found again (except Shift+F12), so minimize to the taskbar instead
    void HideWindow() { if (tray.Visible) Hide(); else WindowState = FormWindowState.Minimized; }

    // borderless, but keeps a drop shadow and the taskbar minimize/restore behaviour
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;          // CS_DROPSHADOW
            cp.Style |= 0x20000 | 0x80000;     // WS_MINIMIZEBOX | WS_SYSMENU
            return cp;
        }
    }

    void ApplyTrayLink(bool on)
    {
        applyingLink = true;
        chkTray.Checked = on;
        applyingLink = false;
        tray.Visible = on;
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
                if (k.GetValue("WindowX") is int wx && k.GetValue("WindowY") is int wy) savedPos = new Point(wx, wy);
            }
            using var r = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (r?.GetValue(LegacyRunName) != null)
            {
                // the old entry points at AutoClicker.exe, which no longer exists after the rename
                r.DeleteValue(LegacyRunName, false);
                r.SetValue(RunName, $"\"{Environment.ProcessPath}\" --autostart");
            }
            // keep "Start with Windows" pointing at this copy (e.g. after PAX Toolkit installs it somewhere new)
            string runValue = $"\"{Environment.ProcessPath}\" --autostart";
            if (r?.GetValue(RunName) is string current && !string.Equals(current, runValue, StringComparison.OrdinalIgnoreCase))
                r.SetValue(RunName, runValue);
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
            // window position (fixed size); RestoreBounds gives the normal spot while minimized/hidden
            var pos = WindowState == FormWindowState.Normal ? Location : RestoreBounds.Location;
            if (IsHandleCreated && PaxPlacement.IsVisible(new Rectangle(pos, Size), bar.BarHeight))
            {
                k.SetValue("WindowX", pos.X, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("WindowY", pos.Y, Microsoft.Win32.RegistryValueKind.DWord);
            }
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
        // Alt+F4 and the taskbar's Close arrive as SC_CLOSE: hide instead of quitting
        else if (m.Msg == 0x0112 && ((int)m.WParam & 0xFFF0) == 0xF060) { HideWindow(); return; }
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
