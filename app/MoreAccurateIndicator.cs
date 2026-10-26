using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Runtime.InteropServices;

internal static class LayeredIndicator
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int x, y; internal Point(int x, int y) { this.x = x; this.y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int width, height; internal Size(int width, int height) { this.width = width; this.height = height; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Blend { internal byte operation, flags, alpha, format; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination, ref Size size, IntPtr source, ref Point origin, int colorKey, ref Blend blend, int flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    internal static void KeepOnTop(IntPtr window)
    {
        // HWND_TOPMOST; preserve geometry and keyboard focus.
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    internal static void Present(Form form, Bitmap bitmap, byte opacity)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr image = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            image = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memory, image);
            Point destination = new Point(form.Left, form.Top), origin = new Point(0, 0);
            Size size = new Size(bitmap.Width, bitmap.Height);
            Blend blend = new Blend { operation = 0, flags = 0, alpha = opacity, format = 1 };
            if (!UpdateLayeredWindow(form.Handle, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (image != IntPtr.Zero) DeleteObject(image);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}

internal static class IndicatorStorage
{
    // AppData can be virtualized differently for Codex Desktop and an Explorer-launched bar.
    // A portable directory beside the executable is shared by both process contexts.
    internal static readonly string DirectoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

    internal static string[] LegacyDirectories()
    {
        List<string> paths = new List<string>();
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoreAccurateIndicator"));
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (string package in Directory.GetDirectories(packages, "OpenAI.Codex_*"))
                    paths.Add(Path.Combine(package, "LocalCache", "Local", "MoreAccurateIndicator"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths.ToArray();
    }
}

internal sealed class IndicatorState
{
    public int understanding;
    public int reliability;
    public string activeMode = "understanding";
    public string note = "";
    public string updatedAt = "";
}

internal sealed class IndicatorPreferences
{
    public string language = "en";
    public bool topmost = true;
    public bool launchAtLogin;
    public bool rememberPosition;
    public bool compactMode;
    public int x = 120;
    public int y = 120;
}

internal sealed class TaskProgressState
{
    public string status = "idle";
    public int? completedSteps;
    public int? totalSteps;
    public string note = "";
    public string updatedAt = "";
}

internal sealed class IndicatorForm : Form
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    private readonly string directory = IndicatorStorage.DirectoryPath;
    private readonly string[] legacyDirectories = IndicatorStorage.LegacyDirectories();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly Timer timer = new Timer();
    private IndicatorState state = new IndicatorState();
    private IndicatorPreferences preferences = new IndicatorPreferences();
    private float flow;
    private float displayedScore;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private float generationReveal, generationVelocity;
    private TaskProgressState task = new TaskProgressState();
    private string lastTaskPayload = "";
    private float taskFraction;
    private float scoreVelocity;
    private float surfaceOpacity;
    private float revealProgress;
    private float shapeScale = 0.96f;
    private float shapeVelocity;
    private float waterTilt;
    private float waterVelocity;
    private float waterEnergy;
    private DateTime lastFrameAt = DateTime.UtcNow;
    private Point dragOffset;
    private bool dragging;
    private bool connected;
    private string lastPayload = "";
    private string stateSource = "";
    private string lastPainted = null;
    private string lastReadError = "";
    private DateTime nextRead = DateTime.MinValue;
    private DateTime updatingUntil = DateTime.MinValue;
    private DateTime nextTopmost = DateTime.MinValue;
    private readonly ToolTip hint = new ToolTip();
    private ToolStripMenuItem loginItem;
    private ToolStripMenuItem rememberItem;
    private readonly CodexTelemetry telemetry;
    private TelemetrySnapshot live = new TelemetrySnapshot();
    private Image modelIcon;

    private readonly string linkedId;
    private LinkedTask linkedTask;
    private bool showTaskName;
    private bool hasTaskPosition;
    private readonly List<Form> menuHiddenForms=new List<Form>();
    internal Action ManageTasks, LinkDesktop, ExitAll, CompactRequested;
    internal Func<IndicatorForm,Point,Point> GroupDrag;
    internal bool RemembersPosition { get { return preferences.rememberPosition && (linkedId==null || hasTaskPosition); } }
    public IndicatorForm() : this(null) { }
    public IndicatorForm(string taskId)
    {
        linkedId=taskId;
        Directory.CreateDirectory(directory);
        string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (String.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        telemetry = new CodexTelemetry(codexHome);
        try { using (Stream icon = typeof(IndicatorForm).Assembly.GetManifestResourceStream("openai.png")) if (icon != null) using (Image source = Image.FromStream(icon)) modelIcon = new Bitmap(source); }
        catch (ArgumentException) { }
        LoadPreferences();
        if (linkedId != null) preferences.compactMode = false;
        preferences.topmost = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = preferences.compactMode ? new Size(110, 110) : ExpandedSize(false);
        TopMost = true;
        Location = ConstrainLocation(new Point(preferences.x, preferences.y), Size, Screen.FromPoint(new Point(preferences.x, preferences.y)).WorkingArea);
        DoubleBuffered = true;
        Text = "把握度助手";
        AccessibleName = "Codex 把握度";

        BuildContextMenu();

        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; Capture = true; dragOffset = e.Location; } };
        MouseMove += (s, e) => { if (dragging) MoveFromPointer(PointToScreen(e.Location)); };
        MouseUp += (s, e) => { dragging = false; Capture = false; if (preferences.rememberPosition) SavePreferences(); };
        MouseCaptureChanged += (s, e) => { if (!Capture) dragging = false; };
        MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleCompactMode(); };

        timer.Interval = 16;
        timer.Tick += (s, e) => {
            if (DateTime.UtcNow >= nextRead) { if(linkedId==null) { LoadState(); LoadTaskProgress(); live = telemetry.Snapshot(); } UpdateTelemetryHint(); nextRead = DateTime.UtcNow.AddMilliseconds(400); }
            if (DateTime.UtcNow >= nextTopmost && !ContextMenuStrip.Visible) { LayeredIndicator.KeepOnTop(Handle); nextTopmost = DateTime.UtcNow.AddSeconds(1); }
            DateTime now = DateTime.UtcNow;
            double elapsed = Math.Max(0, Math.Min(0.25, (now - lastFrameAt).TotalSeconds));
            lastFrameAt = now;
            flow = (flow + (float)elapsed * 1.3f) % 6.283185f;
            int target = state.understanding;
            // Integrate springs in small steps so delayed frames do not change the feel or stability.
            double remaining = elapsed;
            while (remaining > 0)
            {
                float dt = (float)Math.Min(0.008, remaining);
                if (task.status == "running")
                {
                    generationVelocity += ((1f - generationReveal) * 260f - generationVelocity * 19f) * dt;
                    generationReveal += generationVelocity * dt;
                }
                else { generationReveal = 0; generationVelocity = 0; }
                float fraction = task.totalSteps.HasValue ? (float)task.completedSteps.Value / task.totalSteps.Value : 0;
                taskFraction += (fraction - taskFraction) * (1f - (float)Math.Exp(-dt * 12));
                scoreVelocity += ((target - displayedScore) * 170f - scoreVelocity * 21f) * dt;
                displayedScore = Math.Max(0, Math.Min(100, displayedScore + scoreVelocity * dt));
                shapeVelocity += ((1f - shapeScale) * 260f - shapeVelocity * 19f) * dt;
                shapeScale += shapeVelocity * dt;
                waterVelocity += (-waterTilt * 42f - waterVelocity * 4.6f) * dt;
                waterTilt = Math.Max(-12f, Math.Min(12f, waterTilt + waterVelocity * dt));
                remaining -= dt;
            }
            if (Math.Abs(target - displayedScore) < 0.04f && Math.Abs(scoreVelocity) < 0.15f) { displayedScore = target; scoreVelocity = 0; }
            if (Math.Abs(1f - shapeScale) < 0.0005f && Math.Abs(shapeVelocity) < 0.005f) { shapeScale = 1; shapeVelocity = 0; }
            revealProgress = Math.Min(1f, revealProgress + (float)elapsed / 0.36f);
            surfaceOpacity = 255f * revealProgress * revealProgress * (3f - 2f * revealProgress);
            waterEnergy *= (float)Math.Exp(-elapsed * 1.8);
            RenderIndicator();
        };
        Shown += (s, e) => { if(linkedId==null)telemetry.Start(); KeepInsideScreen(); lastFrameAt = DateTime.UtcNow; if(linkedId==null)LoadState(); RenderIndicator(); ShowWindow(Handle, 4); LayeredIndicator.KeepOnTop(Handle); timer.Start(); };
        FormClosed += (s, e) => { if (preferences.rememberPosition) SavePreferences(); };
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing) { timer.Stop(); timer.Dispose(); telemetry.Dispose(); hint.Dispose(); if(modelIcon!=null) { modelIcon.Dispose(); modelIcon=null; } }
        base.Dispose(disposing);
    }

    internal void ApplyLinkedTask(LinkedTask next,bool showName)
    {
        bool changedName=showTaskName!=showName;
        linkedTask=next; showTaskName=showName;
        if(!preferences.compactMode)
        {
            Size desired=ExpandedSize(next.status=="running");
            if(Size!=desired) { ClientSize=desired; KeepInsideScreen(); }
        }
        if(changedName && preferences.compactMode) { Point center=new Point(Left+Width/2,Top+Height/2);ClientSize=DesiredSize();Location=ConstrainLocation(new Point(center.X-Width/2,center.Y-Height/2),Size,Screen.FromPoint(center).WorkingArea); }
        DateTime updated;
        bool fresh=DateTime.TryParse(next.updatedAt,null,System.Globalization.DateTimeStyles.RoundtripKind,out updated)
            && updated.ToUniversalTime()>=System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        string payload=json.Serialize(next);
        if(payload==lastPayload) { if(changedName)lastPainted=null; return; }
        state.understanding=fresh && next.understanding.HasValue?next.understanding.Value:0;
        state.note=next.note;state.updatedAt=next.updatedAt;connected=fresh && next.understanding.HasValue;
        task.status=fresh?next.status:"idle";task.completedSteps=next.completedSteps;task.totalSteps=next.totalSteps;
        if(task.status!="running") { generationReveal=0;generationVelocity=0;taskFraction=0; }
        lastPayload=payload;lastTaskPayload=payload;stateSource="sessions.json#"+next.id;
        if(connected)updatingUntil=DateTime.UtcNow.AddMilliseconds(600);
        lastTelemetryHint="";
    }

    private string Local(string chinese, string english) { return preferences.language == "en" ? english : chinese; }

    private Size ExpandedSize(bool running) { return new Size(390,running?72:48); }
    private Size DesiredSize() { return preferences.compactMode ? (showTaskName ? new Size(196,136) : new Size(110,110)) : ExpandedSize(task.status=="running"); }

    internal static void SetLanguageEverywhere(string language)
    {
        string normalized=language == "en" ? "en" : "zh-CN";
        foreach(Form form in Application.OpenForms)
        {
            IndicatorForm bar=form as IndicatorForm;
            if(bar!=null)bar.ApplyLanguageLocal(normalized);
            TaskWheel wheel=form as TaskWheel;
            if(wheel!=null)wheel.ApplyLanguageLocal(normalized);
        }
    }
    private void ApplyLanguageLocal(string language)
    {
        preferences.language=language;
        SavePreferences(); BuildContextMenu(); lastTelemetryHint=""; UpdateTelemetryHint(); RenderIndicator();
    }

    private void BuildContextMenu()
    {
        ContextMenuStrip previous = ContextMenuStrip;
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Font = Handwriting.Make(true, 12f);
        menu.Items.Add(new ToolStripMenuItem(Local("需求理解度", "Requirement understanding")) { Enabled = false });
        ToolStripMenuItem language = new ToolStripMenuItem("Language");
        ToolStripMenuItem chinese = new ToolStripMenuItem("简体中文") { Checked = preferences.language != "en" };
        ToolStripMenuItem english = new ToolStripMenuItem("English") { Checked = preferences.language == "en" };
        chinese.Click += (s, e) => SetLanguageEverywhere("zh-CN");
        english.Click += (s, e) => SetLanguageEverywhere("en");
        language.DropDownItems.Add(chinese); language.DropDownItems.Add(english); menu.Items.Add(language);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Local("始终置顶", "Always on top")) { Checked = true, Enabled = false });
        loginItem = new ToolStripMenuItem(Local("开机启动", "Launch at login")) { CheckOnClick = true, Checked = preferences.launchAtLogin };
        rememberItem = new ToolStripMenuItem(Local("记住位置", "Remember position")) { CheckOnClick = true, Checked = preferences.rememberPosition };
        loginItem.Click += (s, e) => { preferences.launchAtLogin = loginItem.Checked; SetLaunchAtLogin(loginItem.Checked); SavePreferences(); };
        rememberItem.Click += (s, e) => { preferences.rememberPosition = rememberItem.Checked; SavePreferences(); };
        menu.Items.Add(loginItem); menu.Items.Add(rememberItem); menu.Items.Add(new ToolStripSeparator());
        if(linkedId!=null)
        {
            menu.Items.Add(Local("选择任务（最多四个）", "Choose tasks (up to four)"),null,(s,e)=> { if(ManageTasks!=null)ManageTasks(); });
            menu.Items.Add(Local("连接桌面 AI", "Link desktop AI"),null,(s,e)=> { if(LinkDesktop!=null)LinkDesktop(); });
            menu.Items.Add(Local("复制联动指令", "Copy linking instruction"),null,(s,e)=> { if(linkedTask!=null)Clipboard.SetText(SessionHub.Instruction(linkedTask)); });
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(Local("退出", "Exit"), null, (s, e) => { if(ExitAll!=null)ExitAll();else Close(); });
        menu.Opening += (s, e) => SuspendIndicatorTopmost();
        menu.Closed += (s, e) => RestoreIndicatorTopmost();
        ContextMenuStrip = menu;
        Text = "More Accurate"; AccessibleName = Local("Codex 需求理解度", "Codex requirement understanding");
        if (previous != null) previous.Dispose();
    }

    private void SuspendIndicatorTopmost()
    {
        foreach(Form form in Application.OpenForms)
            if(form!=this && (form is IndicatorForm || form is TaskWheel)) { menuHiddenForms.Add(form); form.Hide(); }
        TopMost=false;
    }

    private void RestoreIndicatorTopmost()
    {
        foreach(Form form in menuHiddenForms.ToArray())
            if(!form.IsDisposed) { form.TopMost=true; form.Show(); }
        menuHiddenForms.Clear();
        TopMost=true;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    private void AddWaterImpulse(float movement)
    {
        if (!preferences.compactMode) return;
        waterVelocity = Math.Max(-110f, Math.Min(110f, waterVelocity - movement * 1.8f));
        waterEnergy = Math.Min(8f, waterEnergy + Math.Abs(movement) * 0.2f);
    }

    private void ToggleCompactMode()
    {
        if (CompactRequested != null) { CompactRequested(); return; }
        dragging = false;
        Capture = false;
        Point center = new Point(Left + Width / 2, Top + Height / 2);
        preferences.compactMode = !preferences.compactMode;
        shapeScale = preferences.compactMode ? 0.78f : 0.94f;
        shapeVelocity = 0;
        ClientSize = DesiredSize();
        Location = ConstrainLocation(new Point(center.X - Width / 2, center.Y - Height / 2), Size, Screen.FromPoint(center).WorkingArea);
        waterVelocity = 0; waterTilt = 0; waterEnergy = 3;
        SavePreferences();
        RenderIndicator();
        LayeredIndicator.KeepOnTop(Handle);
    }

    // All coordinates are in the virtual desktop, including monitors left/above the primary.
    private static Point ConstrainLocation(Point position, Size size, Rectangle area)
    {
        return new Point(Math.Max(area.Left, Math.Min(area.Right - size.Width, position.X)),
            Math.Max(area.Top, Math.Min(area.Bottom - size.Height, position.Y)));
    }

    private void MoveFromPointer(Point pointer)
    {
        Point next = ConstrainLocation(new Point(pointer.X - dragOffset.X, pointer.Y - dragOffset.Y), Size, Screen.FromPoint(pointer).WorkingArea);
        Point requested=new Point(next.X-Left,next.Y-Top);
        Point actual=GroupDrag==null ? requested : GroupDrag(this,requested);
        if(GroupDrag==null)Location=new Point(Left+actual.X,Top+actual.Y);
        AddWaterImpulse(actual.X);
        if (preferences.compactMode) waterEnergy = Math.Min(8f, waterEnergy + Math.Abs(actual.Y) * 0.15f);
    }

    internal void SaveCurrentPosition() { if(preferences.rememberPosition)SavePreferences(); }

    private void KeepInsideScreen()
    {
        Location = ConstrainLocation(Location, Size, Screen.FromRectangle(Bounds).WorkingArea);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        // Reclaim a visible position after unplugging a monitor or changing the taskbar.
        if ((message.Msg == 0x007E || message.Msg == 0x001A || message.Msg == 0x02E0) && !IsDisposed && IsHandleCreated)
            BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) KeepInsideScreen(); });
    }

    protected override CreateParams CreateParams
    {
        get { CreateParams value = base.CreateParams; value.ExStyle |= 0x00080000 | 0x08000000 | 0x00000080; return value; }
    }

    private void RenderIndicator()
    {
        using (Bitmap bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap)) DrawIndicator(graphics);
            LayeredIndicator.Present(this, bitmap, (byte)surfaceOpacity);
        }
        RecordPaint();
    }

    private void LoadState()
    {
        try
        {
            string path = Path.Combine(directory, "state.json");
            DateTime newest = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            // Keep an already-running pre-upgrade MCP server working until Codex restarts.
            foreach (string legacy in legacyDirectories)
            {
                string candidate = Path.Combine(legacy, "state.json");
                if (File.Exists(candidate) && File.GetLastWriteTimeUtc(candidate) > newest)
                { path = candidate; newest = File.GetLastWriteTimeUtc(candidate); }
            }
            if (newest < startedAt) return; // Never present a previous run as a current score.
            string payload = File.ReadAllText(path);
            if (payload == lastPayload) return;
            IndicatorState next = json.Deserialize<IndicatorState>(payload);
            if (next == null) return;
            next.understanding = Math.Max(0, Math.Min(100, next.understanding));
            next.reliability = Math.Max(0, Math.Min(100, next.reliability));
            next.activeMode = "understanding"; // Legacy callers cannot switch the displayed meaning.
            state = next;
            updatingUntil = DateTime.UtcNow.AddMilliseconds(750);
            stateSource = path;
            lastPayload = payload;
            connected = true;
            AccessibleDescription = state.note ?? "";
            hint.SetToolTip(this, (state.note ?? "") + "\n更新时间：" + state.updatedAt);
        }
        catch (Exception exception)
        {
            if (lastReadError != exception.Message)
            {
                lastReadError = exception.Message;
                try { File.WriteAllText(Path.Combine(directory, "read-error.txt"), DateTime.UtcNow.ToString("o") + " " + exception.Message); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void LoadTaskProgress()
    {
        try
        {
            string path = Path.Combine(directory, "task-progress.json");
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < startedAt) return;
            string payload = File.ReadAllText(path);
            if (payload == lastTaskPayload) return;
            TaskProgressState next = json.Deserialize<TaskProgressState>(payload);
            if (next == null || (next.status != "running" && next.status != "complete" && next.status != "interrupted")) return;
            if (next.totalSteps.HasValue && (!next.completedSteps.HasValue || next.totalSteps <= 0 || next.completedSteps < 0 || next.completedSteps > next.totalSteps)) return;
            if (task.status != "running") taskFraction = 0;
            task = next; lastTaskPayload = payload;
            if (task.status != "running") { generationReveal = 0; generationVelocity = 0; }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private void LoadPreferences()
    {
        try
        {
            string path = Path.Combine(directory, linkedId==null || linkedId=="idle" ? "preferences.json" : "preferences-"+linkedId+".json");
            hasTaskPosition=File.Exists(path);
            if (!File.Exists(path) && File.Exists(Path.Combine(directory,"preferences.json"))) path=Path.Combine(directory,"preferences.json");
            if (!File.Exists(path))
                foreach (string legacy in legacyDirectories)
                    if (File.Exists(Path.Combine(legacy, "preferences.json"))) { path = Path.Combine(legacy, "preferences.json"); break; }
            if (File.Exists(path)) preferences = json.Deserialize<IndicatorPreferences>(File.ReadAllText(path)) ?? preferences;
            string global=Path.Combine(directory,"preferences.json");
            if(linkedId!=null && File.Exists(global)) { var shared=json.Deserialize<IndicatorPreferences>(File.ReadAllText(global));if(shared!=null) { preferences.language=shared.language;preferences.launchAtLogin=shared.launchAtLogin;preferences.rememberPosition=shared.rememberPosition; } }
            if (!preferences.rememberPosition) { preferences.x = 120; preferences.y = 120; }
        }
        catch { }
    }

    private void SavePreferences()
    {
        try
        {
            Point position = ConstrainLocation(Location, Size, Screen.FromRectangle(Bounds).WorkingArea);
            preferences.x = position.X;
            preferences.y = position.Y;
            File.WriteAllText(Path.Combine(directory, linkedId==null || linkedId=="idle" ? "preferences.json" : "preferences-"+linkedId+".json"), json.Serialize(preferences));
            if(linkedId!=null && linkedId!="idle")SessionHub.SaveJson("preferences.json",preferences);
        }
        catch { }
    }

    private static void SetLaunchAtLogin(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
        {
            if (key == null) return;
            if (enabled) key.SetValue("MoreAccurateIndicator", "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue("MoreAccurateIndicator", false);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawIndicator(e.Graphics);
    }

    private void DrawIndicator(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.ScaleTransform(shapeScale, shapeScale);
        g.TranslateTransform(-Width / 2f, -Height / 2f);
        if (preferences.compactMode)
        {
            if(showTaskName && linkedTask!=null)
            {
                using(Font nameFont=Handwriting.Make(true,11f))
                using(Brush nameInk=new SolidBrush(Color.FromArgb(20,24,28)))
                using(StringFormat nameFormat=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap })
                    g.DrawString(SessionHub.Name(linkedTask,preferences.language=="en"),nameFont,nameInk,new RectangleF(4,0,Width-8,26),nameFormat);
                g.TranslateTransform((Width-110)/2f,26);
            }
            DrawGeneration(g,true);
            GraphicsState ballState=g.Save(); g.TranslateTransform(12,12); DrawBall(g); g.Restore(ballState);
            return;
        }
        DrawGeneration(g,false);
        // Keep the main bar near the top; only the running state reserves the lower strip.
        g.TranslateTransform(0,-8);
        Rectangle bounds = new Rectangle(12, 10, 366, 42);
        using (GraphicsPath track = RoundedPath(bounds, 21))
        using (Brush trackBrush = new SolidBrush(Color.FromArgb(226, 229, 233)))
        {
            DrawSoftShadow(g,track,5);
            g.FillPath(trackBrush, track);
        }
        int score = state.understanding;
        Rectangle fill = new Rectangle(bounds.Left + 2, bounds.Top + 2, bounds.Width - 4, bounds.Height - 4);
        if (connected && score > 0)
        using (GraphicsPath fillPath = RoundedPath(fill, 19))
        {
            g.SetClip(fillPath);
            g.SetClip(new RectangleF(fill.Left, fill.Top, fill.Width * displayedScore / 100f, fill.Height), CombineMode.Intersect);
            Color[] colors = Palette(score);
            using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(-72, fill.Top, fill.Width + 144, fill.Height), colors[0], colors[colors.Length - 1], LinearGradientMode.Horizontal))
            {
                brush.InterpolationColors = new ColorBlend { Colors = colors, Positions = Positions(colors.Length) };
                brush.TranslateTransform((float)Math.Sin(flow) * 36f, 0);
                g.FillPath(brush, fillPath);
            }
            float shineX = fill.Left - 100 + flow / 6.283185f * (fill.Width + 200);
            using (LinearGradientBrush shine = new LinearGradientBrush(new RectangleF(shineX, fill.Top, 100, fill.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
            {
                shine.InterpolationColors = new ColorBlend {
                    Colors = new[] { Color.FromArgb(0,255,255,255), Color.FromArgb(48,255,255,255), Color.FromArgb(0,255,255,255) },
                    Positions = new[] { 0f,0.5f,1f }
                };
                g.FillRectangle(shine, shineX, fill.Top, 100, fill.Height);
            }
            g.ResetClip();
        }
        using (GraphicsPath outline = RoundedPath(bounds, 21))
        using (Pen border = new Pen(Color.Black, 1.8f)) g.DrawPath(border, outline);
        if(showTaskName && linkedTask!=null)
        using(Font labelFont=Handwriting.Make(true,12f))
        using(Brush labelInk=new SolidBrush(Color.FromArgb(22,27,30)))
        using(StringFormat nameFormat=new StringFormat { LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap })
            g.DrawString(SessionHub.Name(linkedTask,preferences.language=="en"),labelFont,labelInk,new RectangleF(27,bounds.Top,258,bounds.Height),nameFormat);
        using (Font scoreFont = IndicatorFont(true,14f))
        using (Brush dark = new SolidBrush(Color.FromArgb(16,19,24)))
        using (StringFormat verticallyCentered = new StringFormat { LineAlignment = StringAlignment.Center })
        {
            if (!connected) g.DrawString("None",scoreFont,dark,new RectangleF(310,bounds.Top,68,bounds.Height),verticallyCentered);
            else
            {
                if (DateTime.UtcNow < updatingUntil) DrawUpdatingDots(g,336,bounds.Top + bounds.Height / 2f);
                else g.DrawString(((int)Math.Round(displayedScore)) + "%",scoreFont,dark,new RectangleF(310,bounds.Top,68,bounds.Height),verticallyCentered);
            }
        }
    }

    private string LatencyText()
    {
        if (!live.requestLatencyMs.HasValue) return "None";
        double value=live.requestLatencyMs.Value;
        return value<1000 ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)+" ms"
            : (value/1000).ToString(value<10000?"0.00":"0.0",System.Globalization.CultureInfo.InvariantCulture)+" s";
    }

    private string lastTelemetryHint="";
    private void UpdateTelemetryHint()
    {
        string connection = live.network == "有响应" ? Local("有响应", "Responding") : live.network == "请求失败" ? Local("请求失败", "Request failed") : "None";
        string source = live.latencySource == "HTTP 请求耗时" ? Local("HTTP 请求耗时", "HTTP request duration") : live.latencySource == "WebSocket 请求耗时" ? Local("WebSocket 请求耗时", "WebSocket request duration") : "None";
        string generation = live.generation == "running" ? Local("生成中", "Generating") : live.generation == "complete" ? Local("已结束", "Finished") : live.generation == "interrupted" ? Local("已中断", "Interrupted") : "None";
        string text=Local("当前模型：", "Current model: ")+live.model
            +"\n"+Local("连接：", "Connection: ")+connection+Local("（最近请求/响应，90 秒后失效）", " (recent evidence; expires after 90 s)")
            +"\n"+Local("请求延迟：", "Request latency: ")+LatencyText()+" · "+source
            +"\n"+Local("生成状态：", "Generation: ")+generation+"\n"+Local("会话：", "Session: ")+(String.IsNullOrEmpty(live.sessionId)?"None":live.sessionId)
            +"\n"+Local("本机遥测：", "Local telemetry: ")+(live.receiverReady?Local("等待/接收中", "Listening"):String.IsNullOrEmpty(live.receiverError)?"None":Local(live.receiverError,"Local telemetry port unavailable"))
            +"\n"+Local("说明：", "Note: ")+(state.note??"");
        if(linkedTask!=null)text=SessionHub.Name(linkedTask,preferences.language=="en")+"\n"+linkedTask.origin+"\n"+(state.note??"")+"\n"+Local("AI自报的需求理解度，不是答案正确率。", "AI-reported understanding, not answer accuracy.");
        if(text!=lastTelemetryHint) { hint.SetToolTip(this,text); AccessibleDescription=text; lastTelemetryHint=text; }
    }

    private void DrawGeneration(Graphics g,bool ball)
    {
        // Actual task steps are supplied by Codex, never inferred from response tokens.
        if (task.status != "running" || generationReveal <= 0) return;
        GraphicsState saved = g.Save();
        float reveal = Math.Max(0, Math.Min(1.12f, generationReveal));
        bool measured = task.totalSteps.HasValue;
        if (ball)
        {
            // Five clear pixels separate the water ball from the colored arc. No empty track.
            g.TranslateTransform(55,55);
            float scale = 0.94f + Math.Min(1.06f,reveal) * 0.06f;
            g.ScaleTransform(scale,scale);
            using (Pen arc = new Pen(Color.FromArgb((int)(255 * Math.Min(1,reveal)),115,163,150),6f))
            {
                arc.StartCap = arc.EndCap = LineCap.Round;
                float sweep = measured ? Math.Max(0,Math.Min(360,taskFraction * 360)) : 78;
                if (sweep >= 359.9f) g.DrawEllipse(arc,-44,-44,88,88);
                else if (sweep > 0.1f) g.DrawArc(arc,-44,-44,88,88,measured ? -90 : flow / 6.283185f * 360 - 90,sweep);
            }
            g.Restore(saved); return;
        }
        g.TranslateTransform(195,61 - (1 - reveal) * 10);
        g.ScaleTransform(Math.Max(0.05f,reveal),1);
        RectangleF bounds = new RectangleF(-100,-5,200,10);
        using (GraphicsPath track = RoundedPath(bounds, 5))
        using (Brush pale = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1,reveal)),211,218,220)))
        using (Brush progress = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1,reveal)),115,163,150)))
        {
            g.FillPath(pale, track); g.SetClip(track, CombineMode.Intersect);
            float width = measured ? bounds.Width * taskFraction : 58;
            float x = measured ? bounds.Left : bounds.Left - width + (flow / 6.283185f) * (bounds.Width + width);
            g.FillRectangle(progress, x, bounds.Top, width, bounds.Height);
        }
        g.Restore(saved);
    }

    private void DrawBall(Graphics g)
    {
        RectangleF bounds = new RectangleF(8,8,70,70);
        using (GraphicsPath circle = new GraphicsPath())
        {
            circle.AddEllipse(bounds);
            DrawSoftShadow(g,circle,4);
            using (Brush track = new SolidBrush(Color.FromArgb(226,229,233))) g.FillPath(track,circle);
            g.SetClip(circle);
            int target = state.understanding;
            if (connected && displayedScore > 0)
            {
                float level = bounds.Bottom - bounds.Height * displayedScore / 100f;
                List<PointF> surface = new List<PointF>();
                float amplitude = 0.8f + waterEnergy * 0.45f;
                for (float x = bounds.Left - 2; x <= bounds.Right + 2; x += 2)
                {
                    float position = (x - bounds.Left) / bounds.Width;
                    float y = level + waterTilt * (position - 0.5f) + (float)Math.Sin(position * Math.PI * 2.0 + flow * 2) * amplitude;
                    if (displayedScore >= 99.9f) y = bounds.Top - 3;
                    surface.Add(new PointF(x,y));
                }
                using (GraphicsPath water = new GraphicsPath())
                {
                    water.AddLines(surface.ToArray());
                    water.AddLine(surface[surface.Count - 1],new PointF(bounds.Right + 2,bounds.Bottom + 2));
                    water.AddLine(new PointF(bounds.Right + 2,bounds.Bottom + 2),new PointF(bounds.Left - 2,bounds.Bottom + 2));
                    water.CloseFigure();
                    Color[] colors = Palette(target);
                    using (LinearGradientBrush liquid = new LinearGradientBrush(bounds,colors[0],colors[colors.Length - 1],LinearGradientMode.Horizontal))
                    {
                        liquid.InterpolationColors = new ColorBlend { Colors = colors, Positions = Positions(colors.Length) };
                        liquid.TranslateTransform((float)Math.Sin(flow) * 5f,0);
                        g.FillPath(liquid,water);
                    }
                }
                using (Pen ripple = new Pen(Color.FromArgb(145,255,255,255),1.3f)) g.DrawLines(ripple,surface.ToArray());
            }
            g.ResetClip();
            using (Pen border = new Pen(Color.Black,1.8f)) g.DrawPath(border,circle);
        }
        if (connected && DateTime.UtcNow < updatingUntil) { DrawUpdatingDots(g,43,43); return; }
        using (Font font = IndicatorFont(true,connected ? 14f : 9.5f))
        using (Brush ink = new SolidBrush(Color.FromArgb(16,19,24)))
        using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(connected ? ((int)Math.Round(displayedScore)) + "%" : "None",font,ink,bounds,centered);
    }

    private void DrawUpdatingDots(Graphics g,float centerX,float centerY)
    {
        double phase = DateTime.UtcNow.TimeOfDay.TotalSeconds * Math.PI * 2 / 0.9;
        for (int i = 0; i < 3; i++)
        {
            float jump = (float)Math.Max(0,Math.Sin(phase - i * 0.9));
            using (Brush dot = new SolidBrush(Color.FromArgb(150 + (int)(jump * 105),28,32,38)))
                g.FillEllipse(dot,centerX + (i - 1) * 10 - 2.3f,centerY - 2.3f - jump * 4.5f,4.6f,4.6f);
        }
    }

    private static void DrawSoftShadow(Graphics g, GraphicsPath silhouette, int radius)
    {
        GraphicsState saved = g.Save();
        g.TranslateTransform(0,2);
        // Nested translucent contours approximate a small feathered, downward shadow.
        for (int distance = radius; distance >= 1; distance--)
        {
            int alpha = Math.Max(1,(int)(12 * Math.Exp(-distance * distance / 12.0)));
            using (Pen feather = new Pen(Color.FromArgb(alpha,0,0,0),distance * 2f)) g.DrawPath(feather,silhouette);
        }
        using (Brush core = new SolidBrush(Color.FromArgb(16,0,0,0))) g.FillPath(core,silhouette);
        g.Restore(saved);
    }

    private static Font IndicatorFont(bool latin, float size) { return Handwriting.Make(latin,size); }

    private void RecordPaint()
    {
        int score = (int)Math.Round(displayedScore);
        // Record only after the per-pixel-alpha surface was successfully presented.
        bool updating = DateTime.UtcNow < updatingUntil;
        string painted = connected + ":" + lastPayload + ":" + preferences.language + ":" + state.activeMode + ":" + score + ":" + preferences.compactMode + ":" + updating+":"+live.model+":"+live.network+":"+LatencyText()+":"+live.generation+":"+live.receiverReady+":"+lastTaskPayload+":"+(generationReveal>0);
        if (painted != lastPainted)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, linkedId==null ? "display-status.json" : "display-"+linkedId+".json"), json.Serialize(new {
                    processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    version = "1.2.0", appearance = "classic", taskId=linkedId, taskName=linkedTask==null?null:SessionHub.Name(linkedTask,preferences.language=="en"),
                    connected = connected, activeMode = state.activeMode, language = preferences.language, score = connected ? (int?)score : null,
                    topmost = TopMost, rendering = "per-pixel-alpha",
                    compactMode = preferences.compactMode,
                    updating = updating,
                    model = live.model, network = live.network, requestLatencyMs = live.requestLatencyMs,
                    latencySource = live.latencySource, generation = live.generation,
                    generationVisible = false,
                    taskStatus = task.status, taskProgressVisible = task.status == "running" && generationReveal > 0,
                    taskCompletedSteps = task.completedSteps, taskTotalSteps = task.totalSteps,
                    telemetryReady = live.receiverReady, telemetryError = live.receiverError,
                    telemetrySession = live.sessionId,
                    sourcePath = stateSource, stateUpdatedAt = state.updatedAt,
                    paintedAt = DateTime.UtcNow.ToString("o")
                }));
                lastPainted = painted;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static float[] Positions(int count)
    {
        float[] result = new float[count];
        for (int i = 0; i < count; i++) result[i] = i / (float)Math.Max(1, count - 1);
        return result;
    }

    private static Color[] Palette(int score)
    {
        if (score >= 90) return new[] { Color.FromArgb(122, 203, 159), Color.FromArgb(176, 231, 188), Color.FromArgb(141, 220, 198) };
        if (score >= 75) return new[] { Color.FromArgb(197, 185, 230), Color.FromArgb(222, 211, 242), Color.FromArgb(198, 196, 236) };
        if (score >= 50) return new[] { Color.FromArgb(227, 210, 156), Color.FromArgb(241, 229, 185), Color.FromArgb(232, 215, 166) };
        if (score >= 25) return new[] { Color.FromArgb(231, 178, 141), Color.FromArgb(243, 210, 174), Color.FromArgb(234, 189, 160) };
        return new[] { Color.FromArgb(229, 165, 173), Color.FromArgb(242, 189, 193), Color.FromArgb(232, 170, 184) };
    }

    private static GraphicsPath RoundedPath(RectangleF rectangle, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class Program
{
    private static TextReader input;
    private static TextWriter output;
    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Length>0 && args[0]=="chrome-extension://hpojjollcpdcgfmpdnmlollgojpdgaon/") { NativeBridge.Run(); return; }
        if(args.Length>0 && args[0]=="--native-host") { NativeBridge.Run(); return; }
        if(args.Length>1 && args[0]=="--watch-window") { DesktopBridge.Watch(args[1]); return; }
        if (args.Length > 0 && args[0] == "--mcp")
        {
            input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            RunMcpServer();
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool firstInstance;
        using (var instance = new System.Threading.Mutex(true, "Local\\MoreAccurateIndicator", out firstInstance))
        {
            if (!firstInstance) return;
            Application.Run(new MultiIndicatorContext());
        }
    }

    private static object Get(IDictionary<string, object> source, string key)
    {
        object value;
        return source != null && source.TryGetValue(key, out value) ? value : null;
    }

    private static void Send(object value)
    {
        output.WriteLine(new JavaScriptSerializer().Serialize(value));
    }

    private static void RunMcpServer()
    {
        string directory = IndicatorStorage.DirectoryPath;
        string statePath = Path.Combine(directory, "state.json");
        JavaScriptSerializer json = new JavaScriptSerializer();
        string line;
        while ((line = input.ReadLine()) != null)
        {
            IDictionary<string, object> request;
            try { request = json.Deserialize<Dictionary<string, object>>(line); }
            catch { continue; }
            object id = Get(request, "id");
            string method = Convert.ToString(Get(request, "method"));
            if (method == "notifications/initialized") continue;
            if (method == "initialize")
            {
                Send(new { jsonrpc = "2.0", id = id, result = new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "more-accurate-indicator", version = "1.2.0" } } });
            }
            else if (method == "ping")
            {
                Send(new { jsonrpc = "2.0", id = id, result = new { } });
            }
            else if (method == "tools/list")
            {
                object tool = new { name = "update_confidence", description = "Update requirement understanding. Unknown facts must not be invented. Legacy reliability/mode inputs are ignored.", inputSchema = new { type = "object", properties = new { taskId = new { type="string",pattern="^[A-Za-z0-9_-]{1,96}$",description="Stable unique ID for this task/chat; reuse for progress." }, understanding = new { type = "integer", minimum = 0, maximum = 100 }, reliability = new { type = "integer", minimum = 0, maximum = 100 }, activeMode = new { type = "string", @enum = new[] { "understanding", "reliability" } }, note = new { type = "string", maxLength = 240 } }, required = new[] { "understanding" } } };
                object progressTool = new { name = "update_task_progress", description = "Report actual task execution, separate from answering or requirement understanding. Call running when work begins, after real milestones, complete after verification, interrupted on stop/block. Omit both step counts when total work is unknown; never fabricate a percentage.", inputSchema = new { type = "object", properties = new { taskId = new { type="string",pattern="^[A-Za-z0-9_-]{1,96}$" }, status = new { type = "string", @enum = new[] { "running", "complete", "interrupted" } }, completedSteps = new { type = "integer", minimum = 0 }, totalSteps = new { type = "integer", minimum = 1 }, note = new { type = "string", maxLength = 240 } }, required = new[] { "status" } } };
                Send(new { jsonrpc = "2.0", id = id, result = new { tools = new[] { tool, progressTool } } });
            }
            else if (method == "tools/call")
            {
                IDictionary<string, object> parameters = Get(request, "params") as IDictionary<string, object>;
                IDictionary<string, object> arguments = Get(parameters, "arguments") as IDictionary<string, object>;
                string taskId=Convert.ToString(Get(arguments,"taskId"));
                if(!String.IsNullOrEmpty(taskId) && !SessionHub.ValidId(taskId)) { Send(new {jsonrpc="2.0",id=id,result=new {isError=true,content=new[]{new {type="text",text="Invalid taskId."}}}});continue; }
                if (Convert.ToString(Get(parameters,"name")) == "update_task_progress")
                {
                    string status = Convert.ToString(Get(arguments,"status"));
                    int completed=0,total=0;
                    bool hasCompleted=Get(arguments,"completedSteps")!=null, hasTotal=Get(arguments,"totalSteps")!=null;
                    if ((status!="running" && status!="complete" && status!="interrupted") || hasCompleted!=hasTotal || (hasTotal && (!Int32.TryParse(Convert.ToString(Get(arguments,"completedSteps")),out completed) || !Int32.TryParse(Convert.ToString(Get(arguments,"totalSteps")),out total) || total<=0 || completed<0 || completed>total)) || (status=="complete" && hasTotal && completed!=total))
                    {
                        Send(new { jsonrpc="2.0", id=id, result=new { isError=true, content=new[] { new { type="text", text="Expected running/complete/interrupted and either no counts or valid completedSteps <= totalSteps. Complete requires all declared steps finished." } } } });
                        continue;
                    }
                    try
                    {
                        Directory.CreateDirectory(directory);
                        TaskProgressState progress = new TaskProgressState { status=status, completedSteps=hasTotal?(int?)completed:null, totalSteps=hasTotal?(int?)total:null, note=Convert.ToString(Get(arguments,"note"))??"", updatedAt=DateTime.UtcNow.ToString("o") };
                        if(!String.IsNullOrEmpty(taskId))SessionHub.Update(taskId,"Codex","mcp",t=> { t.status=status;t.completedSteps=progress.completedSteps;t.totalSteps=progress.totalSteps;t.note=progress.note; });
                        string path=Path.Combine(directory,"task-progress.json"), temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                        if(String.IsNullOrEmpty(taskId)) {
                            File.WriteAllText(temporary,json.Serialize(progress),new System.Text.UTF8Encoding(false));
                            if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
                        }
                        Send(new { jsonrpc="2.0", id=id, result=new { content=new[] { new { type="text", text="Saved task execution state: "+status+". Finished/interrupted tasks hide the progress indicator." } } } });
                    }
                    catch(Exception exception) { Send(new { jsonrpc="2.0", id=id, result=new { isError=true, content=new[] { new { type="text", text=exception.Message } } } }); }
                    continue;
                }
                int understanding;
                if (Convert.ToString(Get(parameters, "name")) != "update_confidence" || !Int32.TryParse(Convert.ToString(Get(arguments, "understanding")), out understanding) || understanding < 0 || understanding > 100)
                {
                    Send(new { jsonrpc = "2.0", id = id, result = new { isError = true, content = new[] { new { type = "text", text = "Expected understanding from 0 to 100." } } } });
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(directory);
                    IDictionary<string, object> state = new Dictionary<string, object> { { "understanding", understanding }, { "activeMode", "understanding" }, { "note", Convert.ToString(Get(arguments, "note")) ?? "" }, { "updatedAt", DateTime.UtcNow.ToString("o") } };
                    if(!String.IsNullOrEmpty(taskId))SessionHub.Update(taskId,"Codex","mcp",t=> { t.understanding=understanding;t.note=Convert.ToString(Get(arguments,"note"))??""; });
                    string temporaryPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    if(String.IsNullOrEmpty(taskId)) {
                        File.WriteAllText(temporaryPath, json.Serialize(state), new System.Text.UTF8Encoding(false));
                        if (File.Exists(statePath)) File.Replace(temporaryPath, statePath, null);
                        else File.Move(temporaryPath, statePath);
                    }
                    Send(new { jsonrpc = "2.0", id = id, result = new { content = new[] { new { type = "text", text = "Saved: understanding " + understanding + "%. State file: " + statePath + ". Display consumption is recorded separately in display-status.json." } } } });
                }
                catch (Exception exception)
                {
                    Send(new { jsonrpc = "2.0", id = id, result = new { isError = true, content = new[] { new { type = "text", text = exception.Message } } } });
                }
            }
            else if (id != null) Send(new { jsonrpc = "2.0", id = id, error = new { code = -32601, message = "Method not found." } });
        }
    }
}
