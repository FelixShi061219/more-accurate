using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class LinkedTask
{
    public string id, provider, origin, nonce;
    public int number;
    public int? understanding;
    public string note = "";
    public string status = "idle";
    public int? completedSteps, totalSteps;
    public string updatedAt = "";
    public long windowHandle, processStarted;
    public int processId;
    public string windowLabel = "";
    public string lastMarker = "";
}

internal sealed class TaskSelection
{
    public List<string> selected = new List<string>();
    public List<string> dismissed = new List<string>();
    public bool manual;
}

internal static class Handwriting
{
    // Comic Sans is the clearest of the installed handwritten faces; the fallbacks
    // preserve the handwritten character without the long, drifting strokes.
    private static readonly string Latin=Choose(new[] { "Comic Sans MS", "Segoe Print", "Ink Free", "Segoe UI" });
    private static readonly string Chinese=Choose(new[] { "华文行楷", "STXingkai", "楷体", "KaiTi", "华文楷体", "Microsoft YaHei UI" });
    private static string Choose(string[] names)
    {
        foreach (string name in names)
            try { using (FontFamily family = new FontFamily(name)) return family.Name; }
            catch (ArgumentException) { }
        return FontFamily.GenericSansSerif.Name;
    }
    internal static Font Make(bool latin,float size) { return new Font(latin?Latin:Chinese,size,FontStyle.Regular); }
}

internal static class SessionHub
{
    internal static string Root = IndicatorStorage.DirectoryPath;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string GateName
    {
        get { using (SHA256 hash = SHA256.Create()) return "Local\\MoreAccurateSessions-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToLowerInvariant()))).Replace("-", "").Substring(0,24); }
    }
    internal static bool ValidId(string id) { return id != null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,96}$"); }
    internal static string Provider(string name)
    {
        foreach (string p in new[] { "Codex", "ChatGPT", "DeepSeek", "Claude", "Gemini" })
            if (String.Equals(name, p, StringComparison.OrdinalIgnoreCase)) return p;
        throw new ArgumentException("Unsupported provider.");
    }
    private static T Locked<T>(Func<T> action)
    {
        using (Mutex gate = new Mutex(false, GateName))
        {
            bool held = false;
            try { try { held = gate.WaitOne(2000); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("Task store is busy."); return action(); }
            finally { if (held) gate.ReleaseMutex(); }
        }
    }
    private static List<LinkedTask> ReadUnsafe()
    {
        string path = Path.Combine(Root, "sessions.json");
        if (!File.Exists(path)) return new List<LinkedTask>();
        return Json.Deserialize<List<LinkedTask>>(File.ReadAllText(path)) ?? new List<LinkedTask>();
    }
    internal static List<LinkedTask> Read() { return Locked(ReadUnsafe); }
    internal static void SaveJson(string name, object value)
    {
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, name), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, Json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp,path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static LinkedTask Update(string id, string provider, string origin, Action<LinkedTask> change)
    {
        if (!ValidId(id)) throw new ArgumentException("Invalid taskId.");
        provider = Provider(provider);
        if (origin != "web" && origin != "desktop" && origin != "mcp") throw new ArgumentException("Invalid origin.");
        return Locked(delegate {
            List<LinkedTask> tasks = ReadUnsafe();
            LinkedTask task = tasks.Find(t => t.id == id);
            if (task == null)
            {
                if (tasks.Count >= 200) throw new InvalidOperationException("Task history is full; remove unused tasks in the selector.");
                task = new LinkedTask { id = id, provider = provider, origin = origin, nonce = Guid.NewGuid().ToString("N").Substring(0,16),
                    number = tasks.Where(t => t.provider == provider).Select(t => t.number).DefaultIfEmpty(0).Max() + 1 };
                tasks.Add(task);
            }
            if (task.provider != provider || task.origin != origin) throw new ArgumentException("Task identity belongs to a different provider or connection.");
            change(task);
            if (task.understanding.HasValue && (task.understanding < 0 || task.understanding > 100)) throw new ArgumentException("Invalid understanding.");
            if (task.completedSteps.HasValue != task.totalSteps.HasValue || (task.totalSteps.HasValue && (task.totalSteps <= 0 || task.completedSteps < 0 || task.completedSteps > task.totalSteps))) throw new ArgumentException("Invalid milestone counts.");
            if (task.status != "idle" && task.status != "running" && task.status != "complete" && task.status != "interrupted") throw new ArgumentException("Invalid execution state.");
            if (task.status == "complete" && task.totalSteps.HasValue && task.completedSteps != task.totalSteps) throw new ArgumentException("Complete requires all declared milestones.");
            if (task.note == null) task.note = "";
            if (task.note.Length > 240) task.note = task.note.Substring(0,240);
            task.updatedAt = DateTime.UtcNow.ToString("o");
            SaveJson("sessions.json",tasks);
            return task;
        });
    }
    internal static void Remove(string id)
    {
        Locked(delegate { List<LinkedTask> tasks=ReadUnsafe(); tasks.RemoveAll(t=>t.id==id); SaveJson("sessions.json",tasks); return true; });
    }
    internal static TaskSelection Selection()
    {
        try { string path = Path.Combine(Root,"selection.json");
            TaskSelection value=File.Exists(path) ? Json.Deserialize<TaskSelection>(File.ReadAllText(path)) ?? new TaskSelection() : new TaskSelection();
            if(value.selected==null)value.selected=new List<string>();if(value.dismissed==null)value.dismissed=new List<string>();return value; }
        catch (ArgumentException) { return new TaskSelection(); }
    }
    internal static string Name(LinkedTask task, bool english) { return task.provider + (english ? " Task " : " 任务") + task.number; }
    internal static string Instruction(LinkedTask task)
    {
        return "请按正常对话回答，先澄清影响结果的关键未知点，不编造事实、来源或执行结果。每轮在回复开头另起一行输出一个浮窗标记，格式为 [[MA:" + task.nonce + ":U=<需求理解度0到100整数>;P=<idle或running或complete或interrupted>;C=<已完成步骤整数或?>;T=<总步骤正整数或?>]]。将尖括号占位符替换为实际值，不保留尖括号。U仅指需求明确程度，不是正确率；未知要求未澄清不能报100。仅实际执行任务用running，普通聊天及澄清用idle；工作和验证完成用complete；阻塞或等用户用interrupted。无法确定总步骤时C和T都填?，不得编造百分比。状态或真实里程碑变化时可在回复中追加同格式标记。不要输出虚构的进展。仅当前聊天使用此标记。";
    }
    internal static bool ApplyMarker(string id, string text)
    {
        LinkedTask task=Read().Find(t=>t.id==id);
        if (task==null || String.IsNullOrEmpty(text)) return false;
        string pattern = @"\[\[MA:" + System.Text.RegularExpressions.Regex.Escape(task.nonce) + @":U=(\d{1,3});P=(idle|running|complete|interrupted);C=(\d{1,7}|\?);T=(\d{1,7}|\?)\]\]";
        var matches=System.Text.RegularExpressions.Regex.Matches(text,pattern);
        if(matches.Count==0) return false;
        var match=matches[matches.Count-1];
        if(match.Value==task.lastMarker) return false;
        int score=int.Parse(match.Groups[1].Value);
        int? completed=match.Groups[3].Value=="?"?(int?)null:int.Parse(match.Groups[3].Value);
        int? total=match.Groups[4].Value=="?"?(int?)null:int.Parse(match.Groups[4].Value);
        Update(task.id,task.provider,task.origin,delegate(LinkedTask current) {
            current.understanding=score; current.status=match.Groups[2].Value; current.completedSteps=completed; current.totalSteps=total;
            current.lastMarker=match.Value; current.note="AI self-reported marker; not an accuracy guarantee.";
        });
        return true;
    }
}

internal sealed class MultiIndicatorContext : ApplicationContext
{
    private readonly Dictionary<string,IndicatorForm> windows=new Dictionary<string,IndicatorForm>();
    private readonly Dictionary<string,Process> observers=new Dictionary<string,Process>();
    private readonly HashSet<string> failedObservers=new HashSet<string>();
    private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    private readonly DateTime startedAt=DateTime.UtcNow;
    private IndicatorForm idle;
    private TaskWheel wheel;
    private bool grouped;
    private NotifyIcon tray;
    private string trayLanguage="en";
    private bool busy, stopping;
    private DateTime legacyStamp=DateTime.MinValue;
    private DateTime legacyTaskStamp=DateTime.MinValue;
    internal MultiIndicatorContext()
    {
        ResetStartupSelection();
        tray=new NotifyIcon { Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath), Text="More Accurate", Visible=true };
        trayLanguage=ReadLanguage(); BuildTrayMenu(); tray.DoubleClick+=(s,e)=>Choose();
        timer.Interval=700; timer.Tick+=(s,e)=>Refresh(); timer.Start(); Refresh();
    }
    private void ResetStartupSelection()
    {
        try
        {
            TaskSelection fresh=new TaskSelection { manual=false };
            SessionHub.SaveJson("selection.json",fresh);
            SessionHub.SaveJson("view.json",new { grouped=false });
            grouped=false;
        }
        catch(Exception) { grouped=false; }
    }
    private string ReadLanguage()
    {
        try { string path=Path.Combine(SessionHub.Root,"preferences.json"); if(File.Exists(path)) { var value=new JavaScriptSerializer().Deserialize<IndicatorPreferences>(File.ReadAllText(path)); if(value!=null&&value.language=="zh-CN")return "zh-CN"; } } catch(Exception) { }
        return "en";
    }
    private void BuildTrayMenu()
    {
        ContextMenuStrip old=tray.ContextMenuStrip;
        ContextMenuStrip menu=new ContextMenuStrip { Font=SystemFonts.MessageBoxFont };
        bool en=trayLanguage=="en";
        menu.Items.Add(en?"Select tasks":"选择任务",null,(s,e)=>Choose());
        menu.Items.Add(en?"Link desktop AI":"连接桌面 AI",null,(s,e)=>DesktopBridge.Link());
        menu.Items.Add(en?"Exit":"退出",null,(s,e)=>ExitThread());
        tray.ContextMenuStrip=menu; if(old!=null)old.Dispose();
    }
    private void SetGrouped(bool value)
    {
        grouped=value;SessionHub.SaveJson("view.json",new { grouped=value });Refresh();
    }
    private void ClearBars()
    {
        foreach(string id in windows.Keys.ToList()) { IndicatorForm form=windows[id];windows.Remove(id);form.Dispose(); }
    }
    private void ImportLegacy()
    {
        string path=Path.Combine(IndicatorStorage.DirectoryPath,"state.json");
        if(File.Exists(path))
        {
            DateTime stamp=File.GetLastWriteTimeUtc(path);
            if(stamp>legacyStamp && stamp>=startedAt)
            {
                var value=new JavaScriptSerializer().Deserialize<IndicatorState>(File.ReadAllText(path));
                if(value!=null)SessionHub.Update("codex-legacy","Codex","mcp",t=> { t.understanding=value.understanding; t.note=value.note; });
                legacyStamp=stamp;
            }
        }
        string progressPath=Path.Combine(IndicatorStorage.DirectoryPath,"task-progress.json");
        if(File.Exists(progressPath))
        {
            DateTime stamp=File.GetLastWriteTimeUtc(progressPath);
            if(stamp>legacyTaskStamp && stamp>=startedAt)
            {
                var progress=new JavaScriptSerializer().Deserialize<TaskProgressState>(File.ReadAllText(progressPath));
                if(progress!=null)SessionHub.Update("codex-legacy","Codex","mcp",t=> { t.status=progress.status;t.completedSteps=progress.completedSteps;t.totalSteps=progress.totalSteps;t.note=progress.note; });
                legacyTaskStamp=stamp;
            }
        }
    }
    private void Refresh()
    {
        if(busy || stopping)return; busy=true;
        try
        {
            ImportLegacy();
            string currentLanguage=ReadLanguage(); if(currentLanguage!=trayLanguage) { trayLanguage=currentLanguage;BuildTrayMenu(); }
            List<LinkedTask> tasks=SessionHub.Read();
            TaskSelection choice=SessionHub.Selection();
            choice.selected=choice.selected.Where(id=>tasks.Exists(t=>t.id==id)).Distinct().Take(4).ToList();
            bool changed=false;
            if(!choice.manual && choice.selected.Count>1) { choice.selected=choice.selected.Take(1).ToList(); changed=true; }
            // Old sessions stay in the picker; only a new report/connection in
            // this run may populate the otherwise unnamed startup indicator.
            if(!choice.manual && choice.selected.Count==0)
                foreach(LinkedTask task in tasks)
                {
                    DateTime reported;
                    if(!choice.dismissed.Contains(task.id) && DateTime.TryParse(task.updatedAt,null,System.Globalization.DateTimeStyles.RoundtripKind,out reported) && reported.ToUniversalTime()>=startedAt)
                    { choice.selected.Add(task.id);changed=true;break; }
                }
            if(changed)SessionHub.SaveJson("selection.json",choice);
            foreach(string id in windows.Keys.Where(id=>!choice.selected.Contains(id)).ToList()) { IndicatorForm removed=windows[id];windows.Remove(id);removed.Dispose(); }
            foreach(string id in observers.Keys.Where(id=>!choice.selected.Contains(id)).ToList()) { StopObserver(id); }
            if(grouped && choice.selected.Count>0)ClearBars();
            else if(wheel!=null) { wheel.Dispose();wheel=null; }
            int index=0;
            int nextBarY=Screen.PrimaryScreen.WorkingArea.Top+80;
            foreach(string id in choice.selected)
            {
                LinkedTask task=tasks.Find(t=>t.id==id); IndicatorForm form;
                if(!grouped && !windows.TryGetValue(id,out form))
                {
                    form=new IndicatorForm(task.id); form.ManageTasks=Choose; form.LinkDesktop=DesktopBridge.Link;
                    form.ExitAll=()=>ExitThread(); form.CompactRequested=()=>SetGrouped(true);
                    // Bars stay close when idle; a running task gets room for its animated strip.
                    if(!form.RemembersPosition)form.Location=new Point(Screen.PrimaryScreen.WorkingArea.Left+80,nextBarY);
                    form.GroupDrag=MoveBarsTogether;
                    form.FormClosed+=(s,e)=> { if(!stopping && windows.ContainsKey(id)) { windows.Remove(id); TaskSelection c=SessionHub.Selection(); c.selected.Remove(id); if(!c.dismissed.Contains(id))c.dismissed.Add(id); SessionHub.SaveJson("selection.json",c); } };
                    windows[id]=form; form.Show();
                }
                if(!grouped)windows[id].ApplyLinkedTask(task,choice.selected.Count>=2);
                nextBarY += task.status == "running" ? 76 : 50;
                if(task.origin=="desktop" && task.windowHandle!=0 && !failedObservers.Contains(id))
                {
                    Process observer;
                    string heartbeat=Path.Combine(SessionHub.Root,"observer-"+id+".json");
                    if(observers.TryGetValue(id,out observer) && (observer.HasExited || DateTime.UtcNow-(File.Exists(heartbeat)?File.GetLastWriteTimeUtc(heartbeat):observer.StartTime.ToUniversalTime())>TimeSpan.FromSeconds(8)))
                    { StopObserver(id); failedObservers.Add(id); SessionHub.Update(id,task.provider,"desktop",t=> { t.understanding=null;t.status="interrupted";t.note="Accessibility reader stopped. Retry from task selector."; }); }
                    else if(!observers.ContainsKey(id)) observers[id]=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--watch-window "+id) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden });
                }
                index++;
            }
            if(grouped && choice.selected.Count>0)
            {
                if(wheel==null || wheel.IsDisposed) { wheel=new TaskWheel();wheel.Expand=()=>SetGrouped(false);wheel.ManageTasks=Choose;wheel.LinkDesktop=DesktopBridge.Link;wheel.ExitAll=()=>ExitThread();wheel.Apply(choice.selected.Select(id=>tasks.Find(t=>t.id==id)).ToList());wheel.Show(); }
                else wheel.Apply(choice.selected.Select(id=>tasks.Find(t=>t.id==id)).ToList());
            }
            if(choice.selected.Count==0)
            {
                if(idle==null || idle.IsDisposed) { idle=new IndicatorForm("idle"); idle.ManageTasks=Choose; idle.LinkDesktop=DesktopBridge.Link; idle.ExitAll=()=>ExitThread(); idle.Show(); }
            }
            else if(idle!=null) { idle.Dispose(); idle=null; }
            SessionHub.SaveJson("hub-status.json",new { processId=Process.GetCurrentProcess().Id,version="1.2.0", visible=choice.selected.Count, grouped=grouped, surfaces=windows.Count+(wheel==null?0:1)+(idle==null?0:1), taskCount=tasks.Count, maximum=4, selected=choice.selected, observerFailures=failedObservers.ToArray(), updatedAt=DateTime.UtcNow.ToString("o") });
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        catch(ArgumentException) { }
        catch(InvalidOperationException) { }
        finally { busy=false; }
    }
    private void StopObserver(string id)
    {
        Process process=observers[id]; try { if(!process.HasExited)process.Kill(); } catch(InvalidOperationException) { }
        process.Dispose(); observers.Remove(id);
    }

    private Point MoveBarsTogether(IndicatorForm source, Point requested)
    {
        List<IndicatorForm> bars=windows.Values.ToList();
        if(!bars.Contains(source))bars.Add(source);
        int minX=int.MinValue,maxX=int.MaxValue,minY=int.MinValue,maxY=int.MaxValue;
        foreach(IndicatorForm bar in bars)
        {
            Rectangle area=Screen.FromRectangle(bar.Bounds).WorkingArea;
            minX=Math.Max(minX,area.Left-bar.Left); maxX=Math.Min(maxX,area.Right-bar.Width-bar.Left);
            minY=Math.Max(minY,area.Top-bar.Top); maxY=Math.Min(maxY,area.Bottom-bar.Height-bar.Top);
        }
        int dx=Math.Max(minX,Math.Min(maxX,requested.X));
        int dy=Math.Max(minY,Math.Min(maxY,requested.Y));
        foreach(IndicatorForm bar in bars) { bar.Location=new Point(bar.Left+dx,bar.Top+dy);bar.SaveCurrentPosition(); }
        return new Point(dx,dy);
    }
    private void Choose()
    {
        List<LinkedTask> tasks=SessionHub.Read(); TaskSelection choice=SessionHub.Selection();
        string language=ReadLanguage(); bool en=language=="en";
        List<Form> hiddenIndicators=DesktopBridge.SuspendIndicators();
        try
        {
            using(Form picker=new Form { Text=en?"Choose indicators · Up to four":"选择浮窗 · 最多四个", Size=new Size(530,410), StartPosition=FormStartPosition.CenterScreen, Font=SystemFonts.MessageBoxFont, BackColor=Color.FromArgb(242,243,245), TopMost=true })
            using(CheckedListBox list=new CheckedListBox { Dock=DockStyle.Fill, CheckOnClick=true, IntegralHeight=false, BorderStyle=BorderStyle.None, BackColor=picker.BackColor, Font=picker.Font })
            using(FlowLayoutPanel actions=new FlowLayoutPanel { Dock=DockStyle.Bottom, Height=52, FlowDirection=FlowDirection.LeftToRight })
            {
                foreach(LinkedTask task in tasks)list.Items.Add(SessionHub.Name(task,en)+" · "+task.origin+(task.understanding.HasValue?" · "+task.understanding+"%":" · None"),choice.selected.Contains(task.id));
                list.ItemCheck+=(s,e)=> { if(e.NewValue==CheckState.Checked && list.CheckedItems.Count>=4) { e.NewValue=CheckState.Unchecked; System.Media.SystemSounds.Beep.Play(); } };
                Button apply=new Button { Text=en?"Save":"保存", AutoSize=true, Height=36 };
                apply.Click+=(s,e)=> { choice.selected=list.CheckedIndices.Cast<int>().Select(i=>tasks[i].id).ToList(); choice.dismissed=tasks.Where(t=>!choice.selected.Contains(t.id)).Select(t=>t.id).ToList(); choice.manual=true; SessionHub.SaveJson("selection.json",choice); picker.Close(); };
                Button remove=new Button { Text=en?"Remove selected":"移除选中任务", AutoSize=true, Height=36 };
                remove.Click+=(s,e)=> { if(list.SelectedIndex<0)return; string id=tasks[list.SelectedIndex].id; SessionHub.Remove(id); picker.Close(); };
                Button retry=new Button { Text=en?"Retry":"重连", AutoSize=true, Height=36 };
                retry.Click+=(s,e)=> { if(list.SelectedIndex<0)return; string id=tasks[list.SelectedIndex].id; failedObservers.Remove(id); if(observers.ContainsKey(id))StopObserver(id); picker.Close(); };
                actions.Controls.Add(apply);actions.Controls.Add(remove);actions.Controls.Add(retry);picker.Controls.Add(list);picker.Controls.Add(actions);picker.ShowDialog();
            }
        }
        finally
        {
            DesktopBridge.RestoreIndicators(hiddenIndicators);
        }
        Refresh();
    }
    protected override void ExitThreadCore()
    {
        stopping=true;timer.Stop();foreach(string id in observers.Keys.ToList())StopObserver(id);
        ClearBars();if(wheel!=null)wheel.Dispose();if(idle!=null)idle.Dispose();
        tray.Visible=false;tray.Dispose();timer.Dispose();base.ExitThreadCore();
    }
}
