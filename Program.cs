using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Contains("--self-test")) return SelfTest.Run();
// Running an installed copy updates it first, before the single-instance check (a no-op for bin\ builds, offline or
// slow GitHub, PXD_NO_UPDATE=1); the updated copy restarts with the same arguments.
if (pxdUpdate.BeforeStart(App.Repo, App.UpdateAsset, args)) return 0;
App.Run(args);
return 0;

static class App
{
    public const string Id = "pxdAutoClicker";
    public const string Repo = "pxdtek/pxdAutoClicker";
    public const string UpdateAsset = @"^pxdAutoClicker-.*-win-x64\.zip$";

    public static void Run(string[] args)
    {
        // A second launch hands over to the running copy (which shows its window) and exits.
        using var single = SingleInstance.Claim(@"Local\pxdAutoClicker.Instance", @"Local\pxdAutoClicker.Show", signalIfRunning: !args.Contains("--autostart"));
        if (single == null) return;
        // First start after the rename: settings, "Start with Windows" and the Toolkit file move to the new name.
        pxdMigrate.Run();
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

// One-time moves from the app's former name (versions before 1.3.0 went by it). Every step is safe to repeat: it does
// nothing once the new place exists or the old one is gone, and never throws. Only this class names the former places.
static class pxdMigrate
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // The prefix the apps had before the pxd rename.
    public const string FormerPrefix = "Pax";

    // "pxdAutoClicker" -> the same name under the former prefix.
    public static string FormerName(string pxdName) =>
        pxdName.StartsWith("pxd", StringComparison.Ordinal) ? FormerPrefix + pxdName[3..] : pxdName;

    // The former shared folder of the Toolkit link (%APPDATA%\ + the former prefix in capitals).
    static string FormerSharedDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FormerPrefix.ToUpperInvariant());

    // At start-up, before anything reads the settings.
    public static void Run()
    {
        RegistryKey(@"Software\" + FormerName(App.Id), @"Software\" + App.Id);
        if (Environment.ProcessPath is { Length: > 0 } exe)
            RunEntry(RunKey, ApprovedKey, FormerName(App.Id), App.Id, exe);
        // PXD_SHARED_DIR set (tests): the real shared file is left alone.
        if (Environment.GetEnvironmentVariable("PXD_SHARED_DIR") is not { Length: > 0 })
            SharedFile(Path.Combine(FormerSharedDir, "toolkit.json"), pxdLink.FilePath);
    }

    // Moves HKCU\<oldPath> (values and subkeys) to HKCU\<newPath> when only the old one exists. True if it moved.
    public static bool RegistryKey(string oldPath, string newPath)
    {
        try
        {
            var hkcu = Microsoft.Win32.Registry.CurrentUser;
            using (var existing = hkcu.OpenSubKey(newPath))
                if (existing != null) return false;
            using (var old = hkcu.OpenSubKey(oldPath))
            {
                if (old == null) return false;
                using var created = hkcu.CreateSubKey(newPath);
                CopyTree(old, created);
            }
            hkcu.DeleteSubKeyTree(oldPath, throwOnMissingSubKey: false);
            return true;
        }
        catch { return false; }
    }

    static void CopyTree(Microsoft.Win32.RegistryKey from, Microsoft.Win32.RegistryKey to)
    {
        foreach (var name in from.GetValueNames())
            to.SetValue(name, from.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)!, from.GetValueKind(name));
        foreach (var sub in from.GetSubKeyNames())
        {
            using var src = from.OpenSubKey(sub);
            using var dst = to.CreateSubKey(sub);
            if (src != null) CopyTree(src, dst);
        }
    }

    // If the Run value oldName exists under HKCU\<runKey>, replaces it with newName = "exe" + the old arguments, and
    // carries over Task Manager's enabled/disabled state (HKCU\<approvedKey>). Never creates one. True if it moved.
    public static bool RunEntry(string runKey, string approvedKey, string oldName, string newName, string exe)
    {
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (run?.GetValue(oldName) is not string old) return false;
            var args = ArgsOf(old);
            run.SetValue(newName, $"\"{exe}\"" + (args.Length > 0 ? " " + args : ""));
            run.DeleteValue(oldName, throwOnMissingValue: false);
            using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
            if (approved?.GetValue(oldName) is byte[] state)
            {
                approved.SetValue(newName, state, Microsoft.Win32.RegistryValueKind.Binary);
                approved.DeleteValue(oldName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }

    // The arguments after the program in a Run command line.
    public static string ArgsOf(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end < 0 ? "" : c[(end + 1)..].Trim();
        }
        var space = c.IndexOf(' ');
        return space < 0 ? "" : c[(space + 1)..].Trim();
    }

    // Copies the former toolkit.json to the new one (app ids under the former prefix renamed to pxd…) when the new one
    // doesn't exist yet. The old file stays for apps that haven't been updated. True if it copied.
    public static bool SharedFile(string oldFile, string newFile)
    {
        try
        {
            if (File.Exists(newFile) || !File.Exists(oldFile)) return false;
            var text = Regex.Replace(File.ReadAllText(oldFile), "\"" + FormerPrefix + "([A-Za-z]+)\"", m => "\"pxd" + m.Groups[1].Value + "\"");
            Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
            File.WriteAllText(newFile, text);
            return true;
        }
        catch { return false; }
    }
}

// pxd apps update themselves before they open: the same as pxdUpdate in the pxd App Standard (pxdStandard.cs), for
// this WinForms app. Checks the latest release on pxdtek.com, or on GitHub (pxdtek) when pxdtek.com fails or answers with
// a block page (4 s at most), and when it's newer downloads the Windows zip, checks
// its .sha256, swaps the files in the exe's folder (the old ones are set aside as *.pxdold and removed at the next start)
// and starts the new exe with the same arguments. Skipped for bin\ / obj\ builds, --self-test, --no-update and
// PXD_NO_UPDATE=1. PXD_UPDATE_API points it at another server (tests). Never stops the app from opening.
static class pxdUpdate
{
    const string Old = ".pxdold";
    const string WaitEnv = "PXD_UPDATED_FROM";

    // True when an update was applied and the new copy started: this one should exit at once.
    public static bool BeforeStart(string repo, string assetPattern, string[] args, Version? current = null)
    {
        try
        {
            WaitForPrevious();
            var exe = Environment.ProcessPath;
            if (exe is null || !OperatingSystem.IsWindows()) return false;
            var dir = Path.GetDirectoryName(exe)!;
            CleanUp(dir);
            // Already running (this start will just hand over to it): never swap files under a running copy.
            if (OtherCopyRunning(exe)) return false;
            if (Environment.GetEnvironmentVariable("PXD_NO_UPDATE") == "1"
                || args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-update", StringComparison.OrdinalIgnoreCase))
                || dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
                return false;

            current ??= ThisVersion();
            var plan = FindUpdateAsync(repo, assetPattern, current).GetAwaiter().GetResult();
            if (plan is null) return false;
            return ApplyAsync(plan.Value.Zip, plan.Value.Sum, dir, exe, args).GetAwaiter().GetResult();
        }
        catch { return false; }
    }

    // This copy's version: the informational version without the "+commit" part.
    public static Version ThisVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return ParseVersion(info) ?? asm?.GetName().Version ?? new Version(0, 0);
    }

    public static Version? ParseVersion(string? s)
    {
        var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
    }

    // Where release info is asked for, in order: pxdtek.com (it fetches from GitHub on its side, so clients only ever talk
    // to pxdtek.com), then GitHub itself for networks that block pxdtek.com. PXD_UPDATE_API replaces both (tests); unlike
    // the Avalonia copy it may list several servers separated by ';', so the self-test can check the fallback locally.
    public static string[] ApiBases() =>
        Environment.GetEnvironmentVariable("PXD_UPDATE_API") is { Length: > 0 } a
            ? a.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(b => b.TrimEnd('/')).ToArray()
            : ["https://pxdtek.com/gh", "https://api.github.com"];

    // pxdtek.com's copy of a GitHub release download -> the same file on GitHub (the fallback).
    public static string? GitHubCopy(string url)
    {
        var m = Regex.Match(url, @"^https://pxdtek\.com/dl/([^/]+)/([^/]+)/([^/]+)$");
        return m.Success ? $"https://github.com/pxdtek/{m.Groups[1].Value}/releases/download/{m.Groups[2].Value}/{m.Groups[3].Value}" : null;
    }

    static async Task<string> GetWithFallbackAsync(HttpClient http, string url)
    {
        try { return await http.GetStringAsync(url); }
        catch when (GitHubCopy(url) is not null) { return await http.GetStringAsync(GitHubCopy(url)); }
    }

    // The newer release's zip (and checksum) URLs, or null when this copy is current or nothing could be asked.
    public static async Task<(string Zip, string? Sum)?> FindUpdateAsync(string repo, string assetPattern, Version current)
    {
        using var http = Client(TimeSpan.FromSeconds(4));
        JsonDocument? doc = null;
        foreach (var api in ApiBases())
        {
            try
            {
                var parsed = JsonDocument.Parse(await http.GetStringAsync($"{api}/repos/{repo}/releases/latest"));
                if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("tag_name", out _))
                {
                    doc = parsed;
                    break;
                }
                parsed.Dispose();
            }
            catch { /* blocked, offline or not JSON (a filter's block page): try the next */ }
        }
        if (doc is null) return null;
        using var release = doc;
        var root = doc.RootElement;
        if (ParseVersion(root.GetProperty("tag_name").GetString()) is not { } latest || latest <= current) return null;
        string? zip = null, sum = null, zipName = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (zipName is null && Regex.IsMatch(name, assetPattern))
            {
                zipName = name;
                zip = asset.GetProperty("browser_download_url").GetString();
            }
        }
        if (zip is null) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == zipName + ".sha256")
                sum = asset.GetProperty("browser_download_url").GetString();
        return (zip, sum);
    }

    // Downloads, checks, swaps the files in dir and starts the new exe. False if anything failed (nothing left half-done).
    public static async Task<bool> ApplyAsync(string zipUrl, string? sumUrl, string dir, string exe, string[] args, bool start = true)
    {
        var stage = Path.Combine(Path.GetTempPath(), "pxd-update-" + Guid.NewGuid().ToString("N"));
        var moved = new List<string>();
        var added = new List<string>();
        try
        {
            Directory.CreateDirectory(stage);
            var zip = Path.Combine(stage, "update.zip");
            using (var http = Client(TimeSpan.FromMinutes(5)))
            {
                try
                {
                    await using var src = await http.GetStreamAsync(zipUrl);
                    await using var dst = File.Create(zip);
                    await src.CopyToAsync(dst);
                }
                catch when (GitHubCopy(zipUrl) is { } alt)
                {
                    // pxdtek.com blocked or down: the same file straight from GitHub.
                    await using var src = await http.GetStreamAsync(alt);
                    await using var dst = File.Create(zip);
                    await src.CopyToAsync(dst);
                }
                if (sumUrl is not null)
                {
                    var expected = Regex.Match(await GetWithFallbackAsync(http, sumUrl), "[0-9a-fA-F]{64}").Value;
                    await using var fs = File.OpenRead(zip);
                    var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs));
                    if (expected.Length == 64 && !expected.Equals(actual, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }

            var unz = Path.Combine(stage, "x");
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, unz);
            var root = unz;
            if (Directory.GetFiles(unz).Length == 0 && Directory.GetDirectories(unz) is [var only]) root = only;
            if (!File.Exists(Path.Combine(root, Path.GetFileName(exe)))) return false; // not this app's download

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(dir, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    File.Move(dest, dest + Old, overwrite: true);
                    moved.Add(dest);
                }
                else added.Add(dest);
                File.Copy(file, dest);
            }

            if (start)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.Environment[WaitEnv] = Environment.ProcessId.ToString();
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            return true;
        }
        catch
        {
            // Put the old files back.
            foreach (var f in added) try { File.Delete(f); } catch { }
            foreach (var f in moved) try { File.Move(f + Old, f, overwrite: true); } catch { }
            return false;
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
        }
    }

    // Removes the files an update set aside (they are free once the old copy has closed).
    public static void CleanUp(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*" + Old, SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* still in use: next start */ }
        }
        catch { }
    }

    // Another process started from the same exe file is running.
    public static bool OtherCopyRunning(string exe)
    {
        var found = false;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                if (found || p.Id == Environment.ProcessId) continue;
                try { found = string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                catch { found = true; } // can't tell (elevated copy): assume it's ours and leave the files alone
            }
        }
        return found;
    }

    // A copy started by an update waits for the old one to close, so its single-instance mutex is free.
    static void WaitForPrevious()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(WaitEnv), out var pid)) return;
        Environment.SetEnvironmentVariable(WaitEnv, null);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(15000);
        }
        catch { }
    }

    static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("pxd-update/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}

// pxd Toolkit link: %APPDATA%\pxd\toolkit.json -> apps.<id>.trayIcon (default true). PXD_SHARED_DIR overrides the folder.
static class pxdLink
{
    public static string SharedDir =>
        Environment.GetEnvironmentVariable("PXD_SHARED_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxd");

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

static class pxdPlacement
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

// The pxd title bar: 38 px #0A0D12 with a 1 px #1B2030 line under it, the app icon (18 px) and name on the left,
// minimize and close (46x38) at the right. Empty bar area drags the window.
sealed class pxdTitleBar : Control
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

    public pxdTitleBar(string title, Bitmap? icon)
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

// pxd dark theme colours and helpers for the stock WinForms controls.
static class pxdTheme
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
    public static void Numeric(pxdNumericUpDown n)
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
sealed class pxdNumericUpDown : NumericUpDown
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    const int EM_SETMARGINS = 0xD3, EC_LEFTMARGIN = 1;

    public pxdNumericUpDown() { pxdTheme.Numeric(this); }

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
        using var pen = new Pen(ContainsFocus ? pxdTheme.HoverBorder : pxdTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

// Check box drawn in the pxd look: dark box with a #272C39 border, #19F58C fill and dark tick when checked.
sealed class pxdCheckBox : CheckBox
{
    bool hover;

    public pxdCheckBox()
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
        using (var path = pxdTheme.Round(r, 3 * s))
        {
            using var fill = new SolidBrush(Checked ? (hover ? pxdTheme.AccentHover : pxdTheme.Accent) : pxdTheme.Surface);
            g.FillPath(fill, path);
            if (!Checked || (Focused && ShowFocusCues))
            {
                using var pen = new Pen(hover || (Focused && ShowFocusCues) ? pxdTheme.HoverBorder : pxdTheme.Border);
                g.DrawPath(pen, path);
            }
        }
        if (Checked)
        {
            using var tick = new Pen(pxdTheme.OnAccent, 2 * s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(tick, new[] { new PointF(r.X + 3.3f * s, r.Y + 6.8f * s), new PointF(r.X + 5.8f * s, r.Y + 9.3f * s), new PointF(r.X + 10.2f * s, r.Y + 4.2f * s) });
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(box + Gap, 0, Math.Max(0, Width - box - Gap), Height), Enabled ? ForeColor : pxdTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

// Drop-down list drawn dark: the closed box is painted here (also for WM_PRINT), the list items are owner-drawn.
sealed class pxdComboBox : ComboBox
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    const int WM_PAINT = 0x000F, WM_PRINT = 0x0317, WM_PRINTCLIENT = 0x0318;
    bool hover;

    public pxdComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = pxdTheme.Surface;
        ForeColor = pxdTheme.Text;
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
        using (var b = new SolidBrush(sel ? pxdTheme.Raised : pxdTheme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
        if (sel) using (var a = new SolidBrush(pxdTheme.Accent)) e.Graphics.FillRectangle(a, e.Bounds.X, e.Bounds.Y, Math.Max(2, (int)Math.Round(2 * DeviceDpi / 96f)), e.Bounds.Height);
        var r = e.Bounds; r.X += (int)Math.Round(6 * DeviceDpi / 96f);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, sel ? pxdTheme.Text : pxdTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    void PaintBox(Graphics g)
    {
        float s = DeviceDpi / 96f;
        using (var bg = new SolidBrush(pxdTheme.Surface)) g.FillRectangle(bg, 0, 0, Width, Height);
        using (var pen = new Pen(hover || Focused || DroppedDown ? pxdTheme.HoverBorder : pxdTheme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        int arrowW = (int)Math.Round(20 * s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)Math.Round(5 * s), 0, Math.Max(0, Width - arrowW - (int)(5 * s)), Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float cx = Width - arrowW / 2f, cy = Height / 2f, a = 4 * s;
        using var chevron = new Pen(pxdTheme.Muted, 1.3f * s);
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
sealed class pxdTrackBar : TrackBar
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

    public pxdTrackBar() { BackColor = pxdTheme.Back; }

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
                        using (var p = pxdTheme.Round(track, h / 2)) using (var b = new SolidBrush(pxdTheme.Border)) g.FillPath(b, p);
                        var done = new RectangleF(ch.Left, y, Math.Max(h, (th.Left + th.Right) / 2f - ch.Left), h);
                        using (var p = pxdTheme.Round(done, h / 2)) using (var b = new SolidBrush(pxdTheme.Accent)) g.FillPath(b, p);
                        break;
                    }
                    case TBCD_THUMB:
                    {
                        var th = GetRect(TBM_GETTHUMBRECT);
                        var r = new RectangleF(th.Left + 0.5f, th.Top + 1.5f * s, th.Width - 1, th.Height - 3 * s);
                        bool hot = (cd.uItemState & (CDIS_HOT | CDIS_SELECTED)) != 0;
                        using var p = pxdTheme.Round(r, Math.Min(r.Width, r.Height) / 2);
                        using (var b = new SolidBrush(hot ? pxdTheme.AccentHover : pxdTheme.Accent)) g.FillPath(b, p);
                        using (var pen = new Pen(pxdTheme.OnAccent)) g.DrawPath(pen, p);
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
                        using var pen = new Pen(pxdTheme.Tick);
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

    readonly pxdCheckBox chkHold = new();
    readonly pxdCheckBox chkAuto = new() { Text = "Start with Windows (minimized to tray)" };
    readonly pxdCheckBox chkTray = new() { Text = "Show tray icon", Checked = true };
    readonly NotifyIcon tray = new() { Icon = AppIcon(SystemInformation.SmallIconSize), Text = "pxd Auto Clicker (Shift+F12 to open)" };
    readonly Panel body = new() { Dock = DockStyle.Fill };
    readonly pxdTitleBar bar = new("pxd Auto Clicker", AppIcon(new Size(64, 64)).ToBitmap()) { CloseTip = "Hide (use Exit to quit)" };
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
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", AppKey = @"Software\pxdAutoClicker", RunName = "pxdAutoClicker";
    const string LegacyAppKey = @"Software\AutoClicker", LegacyRunName = "AutoClicker"; // the plain names it had before its first rename (1.0.0)
    readonly pxdCheckBox chkRing = new() { Text = "Show circle at each click", Checked = true };
    readonly ClickRing ring = new();
    volatile bool ringOn = true;
    // The ring overlay can't draw over exclusive-fullscreen games, so clicks can also be heard as a soft tick.
    readonly pxdCheckBox chkTick = new() { Text = "Tick sound at each click (works over fullscreen games)" };
    volatile bool tickOn;
    long lastTickMs;
    readonly System.Diagnostics.Stopwatch tickClock = System.Diagnostics.Stopwatch.StartNew();
    // A dot beside the mouse pointer while the clicker is armed (hold mode) or running (start/stop).
    readonly pxdCheckBox chkDot = new() { Text = "Dot by the mouse pointer while it's on", Checked = true };
    readonly CursorDot dot = new();
    bool dotOn = true;
    bool autostartLaunch, muteNextToggle;
    // last state, kept across updates and restarts; null = never saved
    bool? savedHold, savedOpen;
    bool loaded; // settings are only saved once startup has applied them
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 1000 };
    HookProc? hookProc;        // kept as a field so the GC doesn't collect the delegate
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

    readonly pxdNumericUpDown nCps = new() { Minimum = 1, Maximum = 500, Value = 20, Width = 70 };
    readonly pxdNumericUpDown nCount = new() { Minimum = 0, Maximum = 10_000_000, Value = 0, Width = 90 };
    readonly pxdNumericUpDown nStartDelay = new() { Minimum = 0, Maximum = 60, Value = 3, Width = 50 };
    readonly pxdComboBox cbButton = new() { Width = 90 };
    readonly pxdTrackBar tbVolume = new() { Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10, Width = 120, AutoSize = false, Height = 28 };
    readonly Label lblVolume = new() { AutoSize = true, ForeColor = pxdTheme.Muted };
    readonly Button btn = new() { Text = "Start", Width = 120, Height = 36 };
    readonly Label status = new() { AutoSize = false, Text = "Stopped", TextAlign = ContentAlignment.MiddleLeft };
    readonly Button btnExit = new() { Text = "Exit", Width = 70, Height = 36 };
    CancellationTokenSource? cts;
    long clicks;

    public ClickerForm(EventWaitHandle? showRequest = null)
    {
        this.showRequest = showRequest;
        Text = "pxd Auto Clicker";
        Icon = AppIcon(new Size(32, 32));
        ClientSize = new Size(380, 250);
        FormBorderStyle = FormBorderStyle.None; // the pxd title bar replaces the system one
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9);
        TopMost = true;
        Controls.Add(body);
        Controls.Add(bar); // added last so it docks first, above the body
        // pxd dark theme: labels and check boxes take the body's colours; the inputs are styled below
        BackColor = pxdTheme.Back;
        body.BackColor = pxdTheme.Back;
        body.ForeColor = pxdTheme.Text;
        foreach (var b in new[] { btnToggleKey, btnHoldKey, btnModKey }) pxdTheme.RaisedButton(b);
        pxdTheme.PrimaryButton(btn);

        cbButton.Items.AddRange(new object[] { "Left", "Right", "Middle" }); cbButton.SelectedIndex = 0;

        int y = 14;
        void Row(string label, Control c, string? hint = null)
        {
            body.Controls.Add(new Label { Text = label, Left = 14, Top = y + 4, AutoSize = true });
            c.Left = 170; c.Top = y; body.Controls.Add(c);
            if (hint != null) body.Controls.Add(new Label { Text = hint, Left = c.Right + 6, Top = y + 4, AutoSize = true, ForeColor = pxdTheme.Muted });
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
            UpdateDot();
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
        chkDot.Left = 14; chkDot.Top = y; body.Controls.Add(chkDot);
        chkDot.CheckedChanged += (_, _) => { dotOn = chkDot.Checked; UpdateDot(); };
        y += 24;
        chkAuto.Left = 14; chkAuto.Top = y; body.Controls.Add(chkAuto);
        y += 24;
        chkTray.Left = 14; chkTray.Top = y; body.Controls.Add(chkTray);
        y += 28;
        ClientSize = new Size(380, bar.Height + y + 60);

        LoadSettings();
        if (savedPos is { } pos && pxdPlacement.IsVisible(new Rectangle(pos, Size), bar.BarHeight))
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
        // pxd Toolkit (toolkit.json) and the "Show tray icon" box both decide whether the icon shows.
        ApplyTrayLink(pxdLink.TrayIconVisible(App.Id));
        chkTray.CheckedChanged += (_, _) =>
        {
            if (applyingLink) return;
            tray.Visible = chkTray.Checked;
            pxdLink.SetTrayIconVisible(App.Id, chkTray.Checked);
        };
        linkWatch = pxdLink.Watch(() =>
        {
            bool on = pxdLink.TrayIconVisible(App.Id);
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
            // Hold mode comes back the way it was left (updates, restarts, Windows startup). The first ever
            // --autostart turns it on; the start/stop clicker is never resumed on its own.
            var (hold, hide) = StartupState(savedHold, savedOpen, autostartLaunch);
            if (hold) { muteNextToggle = true; chkHold.Checked = true; }
            // (without a tray icon it stays minimized on the taskbar, so it can still be found)
            if (hide) { WindowState = FormWindowState.Minimized; if (tray.Visible) Hide(); }
            loaded = true;
            SaveSoon(); // record the state it started in, in case it's killed before anything changes
        };
        // save shortly after any change rather than only on exit: pxd Toolkit has to kill a copy that's hidden in the
        // tray to update it, which would otherwise lose everything since it started
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        nCps.ValueChanged += (_, _) => SaveSoon();
        cbButton.SelectedIndexChanged += (_, _) => SaveSoon();
        tbVolume.ValueChanged += (_, _) => SaveSoon();
        foreach (var c in new[] { chkHold, chkRing, chkTick, chkDot }) c.CheckedChanged += (_, _) => SaveSoon();
        VisibleChanged += (_, _) => SaveSoon();
        LocationChanged += (_, _) => SaveSoon();

        btn.Left = 14; btn.Top = y + 6; btn.Click += (_, _) => Toggle();
        btnExit.Left = 380 - 14 - btnExit.Width; btnExit.Top = y + 6; btnExit.Click += (_, _) => ExitApp();
        pxdTheme.RaisedButton(btnExit);
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
        // X only hides; Alt+F4 and the taskbar's Close are caught in WndProc. A plain WM_CLOSE (pxd Toolkit
        // closing it to update, Windows shutdown) still quits normally so settings get saved.
        bar.CloseAction = HideWindow;
        FormClosing += (_, _) =>
        {
            showWait?.Unregister(null); linkWatch?.Dispose(); if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook); ring.Close(); dot.Close(); SaveSettings(); tray.Visible = false; cts?.Cancel(); StopHoldMode(); UnregisterHotKey(Handle, HotkeyId); UnregisterHotKey(Handle, ShowHotkeyId);
        };
    }

    void ExitApp() { tray.Visible = false; Close(); }

    // --autostart (Windows startup, pxd Toolkit restarting it after an update) reopens the window only if it was
    // open; launching the exe yourself always shows it. Hold mode is restored either way.
    internal static (bool Hold, bool Hide) StartupState(bool? savedHold, bool? savedOpen, bool autostart) =>
        (savedHold ?? autostart, autostart && savedOpen != true);

    void SaveSoon() { if (!loaded) return; saveTimer.Stop(); saveTimer.Start(); }

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
                chkDot.Checked = dotOn = Convert.ToInt32(k.GetValue("CursorDot", 1)) != 0;
                if (k.GetValue("WindowX") is int wx && k.GetValue("WindowY") is int wy) savedPos = new Point(wx, wy);
                if (k.GetValue("HoldOn") is int ho) savedHold = ho != 0;
                if (k.GetValue("WindowOpen") is int wo) savedOpen = wo != 0;
            }
            using var r = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (r?.GetValue(LegacyRunName) != null)
            {
                // the old entry points at AutoClicker.exe, which no longer exists after the rename
                r.DeleteValue(LegacyRunName, false);
                r.SetValue(RunName, $"\"{Environment.ProcessPath}\" --autostart");
            }
            // keep "Start with Windows" pointing at this copy (e.g. after pxd Toolkit installs it somewhere new)
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
            k.SetValue("CursorDot", dotOn ? 1 : 0);
            k.SetValue("HoldOn", chkHold.Checked ? 1 : 0);
            k.SetValue("WindowOpen", Visible && WindowState != FormWindowState.Minimized ? 1 : 0);
            // window position (fixed size); RestoreBounds gives the normal spot while minimized/hidden
            var pos = WindowState == FormWindowState.Normal ? Location : RestoreBounds.Location;
            if (IsHandleCreated && pxdPlacement.IsVisible(new Rectangle(pos, Size), bar.BarHeight))
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
        SaveSoon(); // a key was rebound
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
        UpdateDot();

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
                BeginInvoke(() => { btn.Text = "Start"; status.Text = $"Stopped ({clicks} clicks)"; cts?.Dispose(); cts = null; UpdateDot(); });
            }
        });
    }

    void SetStatus(string t) => BeginInvoke(() => status.Text = t);

    // amber while the start/stop clicker runs, green while hold mode is armed
    void UpdateDot() => dot.Mode = !dotOn ? CursorDot.State.Off : cts != null ? CursorDot.State.Clicking
        : chkHold.Checked ? CursorDot.State.Armed : CursorDot.State.Off;

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

// Click-through dot that rides just below-right of the mouse pointer while the clicker is on. Drawn with per-pixel
// alpha (UpdateLayeredWindow) so its edges are smooth, and pushed back to the top of the z-order regularly so
// borderless-windowed games that make themselves topmost don't cover it. Exclusive-fullscreen games still can.
class CursorDot : Form
{
    public enum State { Off, Armed, Clicking }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;

    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly int size, offX, offY;
    State mode;
    POINT last = new() { X = int.MinValue };
    int ticks;

    public CursorDot()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        CreateHandle();
        float s = DeviceDpi / 96f;
        size = (int)Math.Round(20 * s);
        // the arrow pointer covers roughly 12x19 px down-right of its tip; sit just past it
        offX = (int)Math.Round(14 * s); offY = (int)Math.Round(16 * s);
        timer.Tick += (_, _) => Follow();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered | transparent (click-through) | toolwindow (no alt-tab) | noactivate | topmost
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
            return cp;
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public State Mode
    {
        get => mode;
        set
        {
            if (value == mode) return;
            mode = value;
            // shown and hidden straight through SetWindowPos, so WinForms' Visible isn't involved
            if (mode == State.Off) { timer.Stop(); SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW); return; }
            Draw(mode == State.Armed ? pxdTheme.Accent : Color.FromArgb(255, 255, 176, 32));
            last.X = int.MinValue;
            Follow();
            timer.Start();
        }
    }

    void Follow()
    {
        if (!GetCursorPos(out var p)) return;
        bool moved = p.X != last.X || p.Y != last.Y;
        if (!moved && ++ticks % 20 != 0) return; // re-assert topmost about 3 times a second even when still
        last = p;
        SetWindowPos(Handle, HWND_TOPMOST, p.X + offX, p.Y + offY, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    void Draw(Color fill)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float c = size / 2f, r = size * 0.3f;
            using (var glow = new SolidBrush(Color.FromArgb(70, fill))) g.FillEllipse(glow, c - r - 3.5f * size / 20, c - r - 3.5f * size / 20, (r + 3.5f * size / 20) * 2, (r + 3.5f * size / 20) * 2);
            using (var edge = new SolidBrush(Color.FromArgb(230, 14, 16, 21))) g.FillEllipse(edge, c - r - 1.5f, c - r - 1.5f, (r + 1.5f) * 2, (r + 1.5f) * 2);
            using (var core = new SolidBrush(fill)) g.FillEllipse(core, c - r, c - r, r * 2, r * 2);
        }
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
        try
        {
            var pos = new POINT { X = Left, Y = Top };
            var sz = new SIZE { W = size, H = size };
            var src = new POINT();
            var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref pos, ref sz, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
        }
        finally
        {
            SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
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

// --self-test: checks the one-time move from the former name without a window, the real settings or the real Run key.
// Uses a scratch registry key (HKCU\Software\pxdAutoClicker.SelfTest-<guid>) and a temp folder, both removed at the end.
// Prints PASS/FAIL lines; the exit code is the number of failures.
static class SelfTest
{
    public static int Run()
    {
        int failed = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok) failed++;
        }

        var tag = App.Id + ".SelfTest-" + Guid.NewGuid().ToString("N");
        var root = @"Software\" + tag;
        var temp = Path.Combine(Path.GetTempPath(), tag);
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        var former = pxdMigrate.FormerName(App.Id);
        try
        {
            Check(former != App.Id && former.EndsWith("AutoClicker", StringComparison.Ordinal), "former name differs from the new one");

            // Settings key: moved once, with its values and subkeys; a second start changes nothing.
            string oldKey = root + @"\" + former, newKey = root + @"\" + App.Id;
            using (var k = hkcu.CreateSubKey(oldKey))
            {
                k.SetValue("Cps", 37);
                k.SetValue("WindowX", -1200);
                using var sub = k.CreateSubKey("Extra");
                sub.SetValue("Note", "kept");
            }
            Check(pxdMigrate.RegistryKey(oldKey, newKey), "settings key moves on the first start");
            using (var o = hkcu.OpenSubKey(oldKey)) Check(o == null, "former settings key is gone");
            using (var n = hkcu.OpenSubKey(newKey))
            {
                Check(n?.GetValue("Cps") is int cps && cps == 37, "settings values kept");
                Check(n?.GetValue("WindowX") is int wx && wx == -1200, "window position kept");
                using var sub = n?.OpenSubKey("Extra");
                Check(sub?.GetValue("Note") as string == "kept", "subkeys kept");
            }
            using (var n = hkcu.CreateSubKey(newKey)) n.SetValue("Cps", 41);
            Check(!pxdMigrate.RegistryKey(oldKey, newKey), "second start: nothing to move");
            using (var k = hkcu.CreateSubKey(oldKey)) k.SetValue("Cps", 99);
            Check(!pxdMigrate.RegistryKey(oldKey, newKey), "former key next to a new one: not moved");
            using (var n = hkcu.OpenSubKey(newKey)) Check(n?.GetValue("Cps") is int c2 && c2 == 41, "new settings never overwritten");

            // Run entry: moved with its arguments and Task Manager's state, pointing at the new exe; never created.
            string run = root + @"\Run", approved = root + @"\Approved";
            const string newExe = @"C:\Users\Someone\Apps\pxdAutoClicker\pxdAutoClicker.exe";
            Check(!pxdMigrate.RunEntry(run, approved, former, App.Id, newExe), "no former Run value: nothing done");
            using (var r = hkcu.OpenSubKey(run)) Check(r?.GetValue(App.Id) == null, "no Run value created");
            using (var r = hkcu.CreateSubKey(run)) r.SetValue(former, $"\"{Path.Combine(@"C:\Old", former + ".exe")}\" --autostart");
            byte[] disabled = [3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];
            using (var a = hkcu.CreateSubKey(approved)) a.SetValue(former, disabled, Microsoft.Win32.RegistryValueKind.Binary);
            Check(pxdMigrate.RunEntry(run, approved, former, App.Id, newExe), "Run value moves on the first start");
            using (var r = hkcu.OpenSubKey(run))
            {
                Check(r?.GetValue(App.Id) as string == $"\"{newExe}\" --autostart", "new Run value points at the new exe with --autostart");
                Check(r?.GetValue(former) == null, "former Run value gone");
            }
            using (var a = hkcu.OpenSubKey(approved))
                Check(a?.GetValue(App.Id) is byte[] st && st.SequenceEqual(disabled) && a.GetValue(former) == null, "Task Manager's disabled state carried over");
            Check(!pxdMigrate.RunEntry(run, approved, former, App.Id, newExe), "second start: Run value left alone");

            // Shared Toolkit file: copied once with the app ids renamed; an existing new file is never replaced.
            string oldFile = Path.Combine(temp, "old", "toolkit.json"), newDir = Path.Combine(temp, "new"), newFile = Path.Combine(newDir, "toolkit.json");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
            var formerApps = new JsonObject
            {
                [former] = new JsonObject { ["trayIcon"] = false },
                [pxdMigrate.FormerName("pxdCommander")] = new JsonObject { ["trayIcon"] = true },
            };
            File.WriteAllText(oldFile, new JsonObject { ["schema"] = 1, ["apps"] = formerApps }.ToJsonString());
            Check(pxdMigrate.SharedFile(oldFile, newFile), "toolkit.json copied on the first start");
            var apps = JsonNode.Parse(File.ReadAllText(newFile))?["apps"] as JsonObject;
            Check(apps?[App.Id]?["trayIcon"]?.GetValue<bool>() == false && apps.ContainsKey("pxdCommander") && !apps.ContainsKey(former), "app ids renamed, values kept");
            Check(File.Exists(oldFile), "former file left for apps not updated yet");
            Environment.SetEnvironmentVariable("PXD_SHARED_DIR", newDir);
            Check(!pxdLink.TrayIconVisible(App.Id), "the link reads the copied setting");
            pxdLink.SetTrayIconVisible(App.Id, true);
            Check(!pxdMigrate.SharedFile(oldFile, newFile), "second start: nothing copied");
            Check(pxdLink.TrayIconVisible(App.Id), "the new file's own setting is kept");
        }
        catch (Exception ex)
        {
            Check(false, "unexpected " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { hkcu.DeleteSubKeyTree(root, throwOnMissingSubKey: false); } catch { }
            try { Directory.Delete(temp, recursive: true); } catch { }
        }

        // Last state across updates and restarts
        Check(ClickerForm.StartupState(null, null, autostart: true) == (true, true), "first --autostart: hold on, in the tray");
        Check(ClickerForm.StartupState(null, null, autostart: false) == (false, false), "first plain launch: hold off, window shown");
        Check(ClickerForm.StartupState(false, false, autostart: true) == (false, true), "--autostart after hold was turned off: stays off");
        Check(ClickerForm.StartupState(true, true, autostart: true) == (true, false), "--autostart after leaving the window open: reopens it");
        Check(ClickerForm.StartupState(true, false, autostart: false) == (true, false), "plain launch: hold restored, window always shown");

        UpdateChecks(Check);

        Console.WriteLine(failed == 0 ? "All self-tests passed." : $"{failed} self-test(s) failed.");
        return failed;
    }

    // Update before start, against a fake release on a local listener and a temp "install" folder; nothing real is updated.
    static void UpdateChecks(Action<bool, string> Check)
    {
        var root = Path.Combine(Path.GetTempPath(), App.Id + ".UpdateTest-" + Guid.NewGuid().ToString("N"));
        System.Net.HttpListener? listener = null;
        Task? serving = null;
        try
        {
            // Pointed at a port nothing listens on first, so even a slip could never reach GitHub.
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", "http://127.0.0.1:1");
            Check(!pxdUpdate.BeforeStart(App.Repo, App.UpdateAsset, ["--self-test"]), "update: --self-test never updates");
            Check(!pxdUpdate.BeforeStart(App.Repo, App.UpdateAsset, []), "update: a bin\\ build (or no server) never updates");

            // Another copy of the same exe running: never update under it. A windowless system exe stands in for it.
            Check(!pxdUpdate.OtherCopyRunning(Environment.ProcessPath!), "update: only this copy running: free to update");
            var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
            using (var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ping, "-n 6 127.0.0.1")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            {
                Thread.Sleep(300);
                Check(pxdUpdate.OtherCopyRunning(ping), "update: another copy running: files left alone");
                try { other.Kill(); } catch { }
            }
            Check(!pxdUpdate.OtherCopyRunning(Path.Combine(Path.GetTempPath(), "pxd-not-running-" + Guid.NewGuid().ToString("N") + ".exe")), "update: a name nothing runs under");

            string app = Path.Combine(root, "app"), pkg = Path.Combine(root, "pkg", App.Id);
            Directory.CreateDirectory(app);
            Directory.CreateDirectory(pkg);
            string exe = Path.Combine(app, App.Id + ".exe");
            File.WriteAllText(exe, "old exe");
            File.WriteAllText(Path.Combine(app, App.Id + ".dll"), "old dll");
            File.WriteAllText(Path.Combine(pkg, App.Id + ".exe"), "new exe");
            File.WriteAllText(Path.Combine(pkg, App.Id + ".dll"), "new dll");
            File.WriteAllText(Path.Combine(pkg, "extra.json"), "new file");
            string asset = App.Id + "-9.0.0-win-x64.zip", zip = Path.Combine(root, asset);
            System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(root, "pkg"), zip);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();

            var prefix = $"http://127.0.0.1:{new Random().Next(41000, 49000)}/";
            listener = new System.Net.HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            var badSum = false;
            var l = listener;
            serving = Task.Run(async () =>
            {
                while (l.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = await l.GetContextAsync(); } catch { return; }
                    var assets = new JsonArray
                    {
                        new JsonObject { ["name"] = asset, ["browser_download_url"] = prefix + "zip" },
                        new JsonObject { ["name"] = asset + ".sha256", ["browser_download_url"] = prefix + "sum" },
                    };
                    byte[] body = ctx.Request.Url!.AbsolutePath switch
                    {
                        "/repos/" + App.Repo + "/releases/latest" => System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["tag_name"] = "v9.0.0", ["assets"] = assets }.ToJsonString()),
                        "/zip" => File.ReadAllBytes(zip),
                        "/sum" => System.Text.Encoding.ASCII.GetBytes((badSum ? new string('0', 64) : hash) + "  " + asset),
                        // A web filter's block page in place of the release info or the download.
                        "/blocked/repos/" + App.Repo + "/releases/latest" or "/html" =>
                            System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><h1>Blocked by your network</h1></body></html>"),
                        _ => [],
                    };
                    if (ctx.Request.Url.AbsolutePath is "/html" || ctx.Request.Url.AbsolutePath.StartsWith("/blocked/")) ctx.Response.ContentType = "text/html";
                    ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            });
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix.TrimEnd('/'));

            var plan = pxdUpdate.FindUpdateAsync(App.Repo, App.UpdateAsset, new Version(1, 3, 1)).GetAwaiter().GetResult();
            Check(plan is { } p && p.Zip == prefix + "zip" && p.Sum == prefix + "sum", "update: newer release found, with its checksum");
            Check(pxdUpdate.FindUpdateAsync(App.Repo, App.UpdateAsset, new Version(9, 0, 0)).GetAwaiter().GetResult() is null, "update: current copy left alone");

            // pxdtek.com first, GitHub as the fallback; a block page is never taken for the real thing.
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", null);
            Check(string.Join("|", pxdUpdate.ApiBases()) == "https://pxdtek.com/gh|https://api.github.com", "update: pxdtek.com asked first, then GitHub");
            Check(pxdUpdate.GitHubCopy("https://pxdtek.com/dl/pxdAutoClicker/v1.3.3/pxdAutoClicker-1.3.3-win-x64.zip")
                  == "https://github.com/pxdtek/pxdAutoClicker/releases/download/v1.3.3/pxdAutoClicker-1.3.3-win-x64.zip"
                  && pxdUpdate.GitHubCopy(prefix + "zip") is null, "update: downloads and checksums fall back to GitHub's copy");
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix + "blocked");
            Check(pxdUpdate.FindUpdateAsync(App.Repo, App.UpdateAsset, new Version(1, 3, 1)).GetAwaiter().GetResult() is null, "update: a block page as release info is never accepted");
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix + "blocked;" + prefix.TrimEnd('/'));
            var viaFallback = pxdUpdate.FindUpdateAsync(App.Repo, App.UpdateAsset, new Version(1, 3, 1)).GetAwaiter().GetResult();
            Check(viaFallback is { } vf && vf.Zip == prefix + "zip", "update: first server answers a block page -> the next one is used");
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", "http://127.0.0.1:1;" + prefix.TrimEnd('/'));
            Check(pxdUpdate.FindUpdateAsync(App.Repo, App.UpdateAsset, new Version(1, 3, 1)).GetAwaiter().GetResult() is { } vd && vd.Zip == prefix + "zip", "update: first server down -> the next one is used");
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix.TrimEnd('/'));
            Check(!pxdUpdate.ApplyAsync(prefix + "html", null, app, exe, ["--autostart"], start: false).GetAwaiter().GetResult()
                  && File.ReadAllText(exe) == "old exe" && !File.Exists(Path.Combine(app, "extra.json")), "update: a block page as the download is never applied");

            badSum = true;
            var refused = !pxdUpdate.ApplyAsync(plan!.Value.Zip, plan.Value.Sum, app, exe, ["--autostart"], start: false).GetAwaiter().GetResult();
            Check(refused && File.ReadAllText(exe) == "old exe" && !File.Exists(Path.Combine(app, "extra.json")), "update: a bad checksum changes nothing");

            badSum = false;
            var ok = pxdUpdate.ApplyAsync(plan.Value.Zip, plan.Value.Sum, app, exe, ["--autostart"], start: false).GetAwaiter().GetResult();
            Check(ok && File.ReadAllText(exe) == "new exe" && File.ReadAllText(Path.Combine(app, App.Id + ".dll")) == "new dll" && File.Exists(Path.Combine(app, "extra.json")), "update: files swapped");
            Check(File.Exists(exe + ".pxdold"), "update: old files set aside as *.pxdold");
            pxdUpdate.CleanUp(app);
            Check(Directory.GetFiles(app, "*.pxdold").Length == 0, "update: set-aside files removed at the next start");
            Check(pxdUpdate.ParseVersion("1.3.1+abc") == new Version(1, 3, 1) && pxdUpdate.ParseVersion("v1.4") == new Version(1, 4, 0), "update: version parse");
        }
        catch (Exception ex)
        {
            Check(false, "update: unexpected " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", null);
            try { listener?.Stop(); } catch { }
            try { serving?.Wait(2000); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
