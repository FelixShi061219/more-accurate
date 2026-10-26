using System;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Runtime.InteropServices;

class LiveCheck
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    static Type type;
    static object Call(Form form, string method, params object[] args) { return type.GetMethod(method, Hidden).Invoke(form, args); }
    static float Field(Form form, string name) { return (float)type.GetField(name, Hidden).GetValue(form); }
    static void DoubleClick(Form form)
    {
        typeof(Control).GetMethod("OnMouseDoubleClick",Hidden).Invoke(form,new object[] { new MouseEventArgs(MouseButtons.Left,2,form.Width/2,form.Height/2,0) });
    }
    static void Assert(bool pass, string message) { if (!pass) throw new Exception(message); }
    static void Inside(Form form)
    {
        Assert(Screen.FromRectangle(form.Bounds).WorkingArea.Contains(form.Bounds), "Window escaped work area: " + form.Bounds);
    }
    static void BoundaryMath()
    {
        var clamp = type.GetMethod("ConstrainLocation", BindingFlags.NonPublic | BindingFlags.Static);
        // Include a monitor left/above the primary and a nonzero taskbar offset.
        foreach (Rectangle area in new[] { new Rectangle(0, 48, 1920, 1032), new Rectangle(-1920, -1080, 1920, 1040) })
        foreach (Size size in new[] { new Size(390,98), new Size(110,110), new Size(396,88) })
        foreach (Point point in new[] { new Point(-100000,-100000), new Point(100000,-100000), new Point(-100000,100000), new Point(100000,100000) })
        {
            Point actual = (Point)clamp.Invoke(null, new object[] { point, size, area });
            Assert(area.Contains(new Rectangle(actual,size)), "Virtual desktop clamp failed.");
            Assert(actual == (Point)clamp.Invoke(null, new object[] {actual,size,area}), "Clamp must be idempotent.");
        }
    }
    static void DragEdges(Form form)
    {
        type.GetField("dragOffset", Hidden).SetValue(form, new Point(form.Width / 2, form.Height / 2));
        foreach (Point pointer in new[] {new Point(-100000,-100000),new Point(100000,-100000),new Point(-100000,100000),new Point(100000,100000)})
        {
            Call(form,"MoveFromPointer",pointer);
            Assert(Screen.FromPoint(pointer).WorkingArea.Contains(form.Bounds), "Dragging escaped screen boundary.");
        }
    }
    static void Capture(Form form, string path, bool ball)
    {
        using (var bitmap = new Bitmap(form.Width,form.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        {
            using (var graphics = Graphics.FromImage(bitmap)) Call(form,"DrawIndicator",graphics);
            Assert(bitmap.GetPixel(0,0).A==0,"Outer area must be fully transparent.");
            Color shadow = bitmap.GetPixel(ball?55:195,ball?93:73);
            Assert(shadow.A>0 && shadow.A<80,"Subtle downward shadow missing or too opaque: "+shadow);
            Color border = bitmap.GetPixel(ball?55:195,ball?20:28);
            if(!String.IsNullOrEmpty(path))bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
            Assert(border.R<12 && border.G<12 && border.B<12 && border.A>160,"Black contour missing: "+border);
            int partial=0, ink=0;
            for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++)
            {
                Color pixel=bitmap.GetPixel(x,y);
                if(pixel.A>0 && pixel.A<255)partial++;
                if(pixel.A>200 && pixel.R<35 && pixel.G<35 && pixel.B<35)ink++;
            }
            Assert(partial>60,"Antialiased alpha boundary missing.");
            Assert(ink>100,"Readable label/percentage missing.");
            if(!String.IsNullOrEmpty(path))bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string data=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data"); Directory.CreateDirectory(data);
        string state=Path.Combine(data,"state.json");
        string taskPath=Path.Combine(data,"task-progress.json");
        File.WriteAllText(taskPath,"{\"status\":\"running\",\"completedSteps\":2,\"totalSteps\":4}");
        File.WriteAllText(Path.Combine(data,"preferences.json"),"{\"topmost\":false,\"rememberPosition\":true,\"x\":-10000,\"y\":-10000}");
        File.WriteAllText(state,"{\"understanding\":17,\"reliability\":17,\"activeMode\":\"reliability\",\"updatedAt\":\"first\"}");
        type=Assembly.LoadFrom(args[0]).GetType("IndicatorForm",true);
        BoundaryMath();
        using(var form=(Form)Activator.CreateInstance(type,true)) using(var timer=new Timer())
        {
            Inside(form); // Offscreen remembered coordinates have already been repaired.
            int stage=0, frames=0; float initialEnergy=0; Exception failure=null; DateTime stageStart=DateTime.UtcNow;
            timer.Interval=25;
            timer.Tick+=(s,e)=> {
                try
                {
                    Inside(form); // Validate every sampled frame including spring animation.
                    frames++;
                    double elapsed=(DateTime.UtcNow-stageStart).TotalSeconds;
                    if(elapsed<1.6)return;
                    string status=File.ReadAllText(Path.Combine(data,"display-status.json"));
                    if(stage==0)
                    {
                        Assert(status.Contains("\"score\":null") && status.Contains("\"connected\":false"),"Startup must ignore stale scores: "+status);
                        Assert(status.Contains("\"taskProgressVisible\":false"),"Startup must ignore stale task progress.");
                        foreach (ToolStripItem item in form.ContextMenuStrip.Items) Assert(!item.Text.Contains("答案可靠度"),"Removed mode remains in menu.");
                        File.WriteAllText(state,"{\"understanding\":63,\"reliability\":12,\"activeMode\":\"reliability\",\"note\":\"timer test\",\"updatedAt\":\"second\"}");
                    }
                    else if(stage==1)
                    {
                        Assert(status.Contains("\"score\":63"),"Timer failed to refresh state: "+status);
                        Assert(form.FormBorderStyle==FormBorderStyle.None && !form.ShowInTaskbar && form.TopMost,"Chrome/topmost regression.");
                        int style=GetWindowLong(form.Handle,-20);
                        Assert((style&0x8)!=0 && (style&0x80000)!=0,"Native topmost/layered flags missing.");
                        Assert(status.Contains("\"activeMode\":\"understanding\""),"Legacy mode must not change metric.");
                        var observer=type.GetField("telemetry",Hidden).GetValue(form);
                        var current=observer.GetType().GetField("current",Hidden).GetValue(observer);
                        current.GetType().GetField("generation").SetValue(current,"running");
                        using(var blank=new Bitmap(390,98)) {
                            using(var g=Graphics.FromImage(blank)) Call(form,"DrawGeneration",g,false);
                            for(int y=0;y<blank.Height;y++) for(int x=0;x<blank.Width;x++) Assert(blank.GetPixel(x,y).A==0,"Answer generation must not imply task execution.");
                        }
                        File.WriteAllText(taskPath,"{\"status\":\"running\",\"completedSteps\":1,\"totalSteps\":4}");
                        Call(form,"SetLanguage","en");
                        Assert(form.ContextMenuStrip.Items[3].Text=="Always on top","English menu did not apply immediately.");
                        Assert(File.ReadAllText(Path.Combine(data,"preferences.json")).Contains("\"language\":\"en\""),"Language was not persisted.");
                        Call(form,"SetLanguage","zh-CN");
                        Assert(form.ContextMenuStrip.Items[3].Text=="始终置顶","Chinese menu did not restore.");
                        Capture(form,args.Length>1?args[1]:null,false);
                        DragEdges(form);
                        DoubleClick(form);
                        Assert(form.ClientSize==new Size(110,110),"Double-click compact mode failed.");
                        Call(form,"AddWaterImpulse",24f);
                        Assert(Field(form,"waterVelocity")!=0,"Dragging did not excite water.");
                        initialEnergy=Field(form,"waterEnergy");
                    }
                    else if(stage==2)
                    {
                        Assert(form.ClientSize==new Size(110,110),"Ball morph failed to settle.");
                        Assert(Field(form,"generationReveal")>0.99f && Field(form,"generationReveal")<1.01f,"Generation spring did not settle.");
                        Assert(status.Contains("\"taskProgressVisible\":true"),"Running task missing.");
                        Assert(Math.Abs(Field(form,"taskFraction")-0.25f)<0.002f,"Real step fraction missing.");
                        using(var ring=new Bitmap(110,110)) {
                            using(var g=Graphics.FromImage(ring)) Call(form,"DrawGeneration",g,true);
                            Assert(ring.GetPixel(55,11).A>100,"Completed arc missing.");
                            Assert(ring.GetPixel(55,99).A==0,"Unfinished ring must be transparent, without a white track.");
                            Assert(ring.GetPixel(55,17).A==0,"Ring must not touch the main ball.");
                        }
                        var observer=type.GetField("telemetry",Hidden).GetValue(form);
                        var current=observer.GetType().GetField("current",Hidden).GetValue(observer);
                        current.GetType().GetField("generation").SetValue(current,"complete");
                        Assert(Field(form,"waterEnergy")<initialEnergy,"Water did not settle.");
                        Assert(Math.Abs(Field(form,"shapeScale")-1)<0.001f,"Shape spring did not settle.");
                        Capture(form,args.Length>1?Path.Combine(Path.GetDirectoryName(args[1]),Path.GetFileNameWithoutExtension(args[1])+"-ball.png"):null,true);
                        File.WriteAllText(taskPath,"{\"status\":\"complete\",\"completedSteps\":4,\"totalSteps\":4}");
                        DragEdges(form); // End at the bottom-right edge, then expand there.
                        DoubleClick(form);
                    }
                    else if(stage==3)
                    {
                        Assert(form.ClientSize==new Size(390,98),"Bar restore did not settle.");
                        Assert(status.Contains("\"taskProgressVisible\":false"),"Completed task strip must disappear.");
                        using (var blank=new Bitmap(390,120)) {
                            using(var g=Graphics.FromImage(blank)) Call(form,"DrawGeneration",g,false);
                            for(int y=0;y<blank.Height;y++) for(int x=0;x<blank.Width;x++) Assert(blank.GetPixel(x,y).A==0,"Completed strip painted pixels.");
                        }
                        DragEdges(form);
                        // Rapid double-click reversal should return to the bar.
                        DoubleClick(form);
                        stageStart=DateTime.UtcNow.AddSeconds(-1.4); stage=4; return;
                    }
                    else if(stage==4)
                    {
                        DoubleClick(form);
                    }
                    else
                    {
                        Assert(form.ClientSize==new Size(390,98),"Rapid reversal failed.");
                        form.Location=new Point(-10000,-10000);
                        // Same recovery invoked after display/work-area notifications.
                        Call(form,"KeepInsideScreen"); Inside(form);
                        Assert(frames>50,"Animation was not sampled sufficiently.");
                        Console.WriteLine("PASS: actual step progress; dialogue-only hidden; stale task ignored; separated arc with transparent unfinished sector; completed task hidden; bilingual menu; startup None; topmost/alpha; damped water; boundary limits including negative monitor coordinates.");
                        timer.Stop(); form.Close(); return;
                    }
                    stage++; stageStart=DateTime.UtcNow;
                }
                catch(Exception error){failure=error;timer.Stop();form.Close();}
            };
            form.Shown+=(s,e)=> { ShowWindow(form.Handle,0);timer.Start(); };
            Application.Run(form);
            if(failure!=null){Console.Error.WriteLine(failure);return 1;}return 0;
        }
    }
}
