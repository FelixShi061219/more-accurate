using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class DesktopBridge
{
    private delegate bool EnumWindow(IntPtr handle, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint pid);
    private sealed class Candidate
    {
        internal IntPtr handle; internal int pid; internal string title; internal long started;
        public override string ToString() { return title; }
    }

    // Modal dialogs must be above the click-through indicator surfaces. The
    // indicators periodically reassert TOPMOST for normal operation, so hide
    // them for the complete lifetime of a dialog and restore them afterwards.
    internal static List<Form> SuspendIndicators()
    {
        List<Form> hidden = new List<Form>();
        foreach (Form current in Application.OpenForms)
        {
            if ((current is IndicatorForm || current is TaskWheel) && current.Visible)
            {
                hidden.Add(current);
                current.TopMost = false;
                current.Hide();
            }
        }
        return hidden;
    }

    internal static void RestoreIndicators(List<Form> hidden)
    {
        if (hidden == null) return;
        foreach (Form current in hidden.ToArray())
        {
            if (current.IsDisposed) continue;
            current.TopMost = true;
            current.Show();
        }
    }

    internal static void Link()
    {
        bool en=true;
        try { string pref=Path.Combine(IndicatorStorage.DirectoryPath,"preferences.json"); if(File.Exists(pref)) { var value=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<IndicatorPreferences>(File.ReadAllText(pref)); en=value==null||value.language!="zh-CN"; } } catch(Exception) { }
        List<Candidate> candidates=new List<Candidate>();
        EnumWindows(delegate(IntPtr handle,IntPtr ignored) {
            if(!IsWindowVisible(handle))return true; uint pid;GetWindowThreadProcessId(handle,out pid);
            if(pid==Process.GetCurrentProcess().Id)return true;
            StringBuilder text=new StringBuilder(256);GetWindowText(handle,text,text.Capacity);if(text.Length==0)return true;
            if(!IsLikelyAiWindow(text.ToString())) return true;
            try { using(Process process=Process.GetProcessById((int)pid)) candidates.Add(new Candidate { handle=handle,pid=(int)pid,started=process.StartTime.ToUniversalTime().Ticks,title=text.ToString() }); }
            catch(ArgumentException) { }catch(System.ComponentModel.Win32Exception) { }catch(InvalidOperationException) { }
            return true;
        },IntPtr.Zero);
        List<Form> hiddenIndicators = SuspendIndicators();
        try
        {
            using(Form form=new Form { Text=en?"Link desktop AI":"连接桌面 AI",ClientSize=new Size(600,290),MinimumSize=new Size(616,329),StartPosition=FormStartPosition.CenterScreen,TopMost=true,Font=SystemFonts.MessageBoxFont,BackColor=Color.FromArgb(242,243,245),AutoScaleMode=AutoScaleMode.Font,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false })
            {
            Label explanation=new Label { Text=en?"Select an AI window, then paste the copied instruction into that chat.":"选择 AI 窗口，然后把复制的指令粘贴到该聊天。",Left=18,Top=18,Width=570,Height=48,AutoEllipsis=false };
            Label providerLabel=new Label { Text=en?"AI provider":"AI 服务",Left=18,Top=78,Width=170,Height=26,AutoSize=false,TextAlign=ContentAlignment.MiddleLeft };
            Label windowLabel=new Label { Text=en?"Detected window":"检测到的窗口",Left=198,Top=78,Width=390,Height=26,AutoSize=false,TextAlign=ContentAlignment.MiddleLeft };
            ComboBox provider=new ComboBox { Left=18,Top=106,Width=170,Height=32,DropDownStyle=ComboBoxStyle.DropDownList };
            provider.Items.AddRange(new object[]{"DeepSeek","ChatGPT","Codex","Claude","Gemini"});provider.SelectedIndex=0;
            ComboBox window=new ComboBox { Left=198,Top=106,Width=390,Height=32,DropDownStyle=ComboBoxStyle.DropDownList };
            foreach(Candidate candidate in candidates)window.Items.Add(candidate);
            Func<string,string> detectProvider=title => {
                string value=(title??"").ToLowerInvariant();
                if(value.Contains("deepseek"))return "DeepSeek";
                if(value.Contains("chatgpt")||value.Contains("openai"))return "ChatGPT";
                if(value.Contains("codex"))return "Codex";
                if(value.Contains("claude"))return "Claude";
                if(value.Contains("gemini"))return "Gemini";
                return null;
            };
            Action syncProvider=delegate {
                Candidate candidate=window.SelectedItem as Candidate;
                string detected=candidate==null?null:detectProvider(candidate.title);
                int detectedIndex=detected==null?-1:provider.Items.IndexOf(detected);
                if(detectedIndex>=0)provider.SelectedIndex=detectedIndex;
            };
            window.SelectedIndexChanged+=(s,e)=>syncProvider();
            if(window.Items.Count>0)window.SelectedIndex=0;
            Label status=new Label { Text=en?"Only the selected window is read. Missing scores remain None.":"只读取你选择的窗口；读不到评分时显示 None。",Left=18,Top=158,Width=570,Height=48,AutoEllipsis=false };
            Button copy=new Button { Text=en?"Create task and copy instruction":"创建任务并复制指令",Left=18,Top=224,Width=570,Height=42,Enabled=candidates.Count>0 };
            copy.Click+=(s,e)=> {
                Candidate candidate=window.SelectedItem as Candidate;if(candidate==null)return;
                try {
                    string selectedProvider=provider.Text;
                    LinkedTask task=SessionHub.Update("desktop-"+Guid.NewGuid().ToString("N"),selectedProvider,"desktop",t=> { t.windowHandle=candidate.handle.ToInt64();t.processId=candidate.pid;t.processStarted=candidate.started; });
                    Clipboard.SetText(SessionHub.Instruction(task));form.Close();
                }catch(Exception error) { status.Text=(en?"Not linked: ":"未连接：")+error.Message; }
            };
                // Auto-sized text rows use the actual font metrics at the user's
                // scale. Fixed-height labels could clip Chinese glyphs at 150%.
                TableLayoutPanel layout=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,RowCount=5,Padding=new Padding(18),Width=600 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,70));
                foreach(Label label in new[]{explanation,providerLabel,windowLabel,status})
                { label.AutoSize=true;label.Dock=DockStyle.Fill;label.Margin=new Padding(0,6,0,10);label.Padding=new Padding(0,2,0,2); }
                explanation.MaximumSize=new Size(560,0);status.MaximumSize=new Size(560,0);
                provider.Dock=DockStyle.Fill;provider.Margin=new Padding(0,0,12,14);
                window.Dock=DockStyle.Fill;window.Margin=new Padding(0,0,0,14);
                copy.Dock=DockStyle.Fill;copy.AutoSize=true;copy.Padding=new Padding(8);copy.Margin=new Padding(0,10,0,0);
                layout.Controls.Add(explanation,0,0);layout.SetColumnSpan(explanation,2);
                layout.Controls.Add(providerLabel,0,1);layout.Controls.Add(windowLabel,1,1);
                layout.Controls.Add(provider,0,2);layout.Controls.Add(window,1,2);
                layout.Controls.Add(status,0,3);layout.SetColumnSpan(status,2);
                layout.Controls.Add(copy,0,4);layout.SetColumnSpan(copy,2);
                form.Controls.Add(layout);form.ShowDialog();
            }
        }
        finally
        {
            RestoreIndicators(hiddenIndicators);
        }
    }
    private static bool IsLikelyAiWindow(string title)
    {
        string value=(title??"").ToLowerInvariant();
        if(value.Contains("computer use cursor overlay") || value.Contains("is using your computer"))return false;
        foreach(string token in new[]{"codex","chatgpt","deepseek","claude","gemini","openai"})
            if(value.Contains(token)) return true;
        return false;
    }
    internal static void Watch(string id)
    {
        LinkedTask task=SessionHub.Read().Find(t=>t.id==id);if(task==null || task.origin!="desktop")return;
        string heartbeat=Path.Combine(SessionHub.Root,"observer-"+id+".json");
        try
        {
            while(true)
            {
                IntPtr handle=new IntPtr(task.windowHandle);uint pid;
                if(!IsWindow(handle))break;GetWindowThreadProcessId(handle,out pid);
                if(pid!=task.processId)break;
                using(Process process=Process.GetProcessById(task.processId)) if(process.StartTime.ToUniversalTime().Ticks!=task.processStarted)break;
                AutomationElement root=AutomationElement.FromHandle(handle);
                StringBuilder text=new StringBuilder();Stopwatch budget=Stopwatch.StartNew();int nodes=0;
                Queue<AutomationElement> pending=new Queue<AutomationElement>();pending.Enqueue(root);
                while(pending.Count>0 && nodes++<1800 && text.Length<180000 && budget.ElapsedMilliseconds<500)
                {
                    AutomationElement element=pending.Dequeue();
                    try {
                        string name=element.Current.Name;if(!String.IsNullOrEmpty(name))text.AppendLine(name.Length>6000?name.Substring(0,6000):name);
                        object pattern;
                        if(element.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) {
                            string content=((TextPattern)pattern).DocumentRange.GetText(100000);if(content!=null)text.AppendLine(content);
                        }
                        AutomationElement child=TreeWalker.ControlViewWalker.GetFirstChild(element);
                        int children=0;while(child!=null && children++<200 && pending.Count<1800) { pending.Enqueue(child);child=TreeWalker.ControlViewWalker.GetNextSibling(child); }
                    }catch(ElementNotAvailableException) { }catch(InvalidOperationException) { }
                }
                try { SessionHub.ApplyMarker(id,text.ToString()); }catch(ArgumentException) { }
                SessionHub.SaveJson("observer-"+id+".json",new { updatedAt=DateTime.UtcNow.ToString("o"), processId=Process.GetCurrentProcess().Id });
                Thread.Sleep(1500);
                if(!File.Exists(Path.Combine(SessionHub.Root,"sessions.json")) || SessionHub.Read().Find(t=>t.id==id)==null)break;
            }
        }
        catch(Exception) { }
        finally
        {
            try { if(SessionHub.Read().Exists(t=>t.id==id))SessionHub.Update(id,task.provider,"desktop",t=> { t.understanding=null;t.status="interrupted";t.note="Selected window is closed or inaccessible. Reconnect in task selector."; }); }catch(Exception) { }
            try { if(File.Exists(heartbeat))File.Delete(heartbeat); }catch(IOException) { }
        }
    }
}
