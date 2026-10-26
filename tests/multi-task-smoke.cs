using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal class MultiCheck
{
    const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Assert(bool pass,string text) { if(!pass)throw new Exception(text); }
    static object Field(object value,string name) { return value.GetType().GetField(name,Hidden).GetValue(value); }
    static object Call(object value,string name,params object[] args) { return value.GetType().GetMethod(name,Hidden).Invoke(value,args); }
    static LinkedTask Make(int i)
    {
        return SessionHub.Update("fixture-"+i,i%2==0?"Codex":"DeepSeek","mcp",t=> { t.understanding=25+i*14;t.status="running";t.completedSteps=i;t.totalSteps=6; });
    }
    static void RenderColorExamples(string output)
    {
        using(IndicatorForm bar=new IndicatorForm("color-preview"))
        foreach(int score in new[]{20,40,65,82,96})
        {
            bar.ApplyLinkedTask(new LinkedTask { id="color-preview",provider="Codex",number=1,understanding=score,status="idle",updatedAt=DateTime.UtcNow.ToString("o") },false);
            typeof(IndicatorForm).GetField("displayedScore",Hidden).SetValue(bar,(float)score);
            typeof(IndicatorForm).GetField("updatingUntil",Hidden).SetValue(bar,DateTime.MinValue);
            typeof(IndicatorForm).GetField("shapeScale",Hidden).SetValue(bar,1f);
            using(Bitmap bitmap=new Bitmap(bar.Width,bar.Height))
            {
                using(Graphics g=Graphics.FromImage(bitmap))Call(bar,"DrawIndicator",g);
                bitmap.Save(Path.Combine(output,"understanding-"+score+".png"));
            }
        }
    }
    static void DrawAndCheck(TaskWheel wheel,int count,string output)
    {
        List<LinkedTask> tasks=Enumerable.Range(1,count).Select(Make).ToList();wheel.Apply(tasks);
        ((Dictionary<string,DateTime>)Field(wheel,"updating")).Clear();
        var levels=(Dictionary<string,float>)Field(wheel,"levels");var progress=(Dictionary<string,float>)Field(wheel,"progress");
        foreach(LinkedTask task in tasks) { levels[task.id]=task.understanding.Value;progress[task.id]=(float)task.completedSteps.Value/task.totalSteps.Value; }
        using(Bitmap bitmap=new Bitmap(300,300))
        {
            using(Graphics g=Graphics.FromImage(bitmap))Call(wheel,"Draw",g);
            Assert(bitmap.GetPixel(0,0).A==0,"Window background must be transparent");
            if(count>1)
            {
                Assert(bitmap.GetPixel(150,150).A==0,"Sector center must have a transparent gap");
                for(int index=0;index<count;index++)
                {
                    double angle=(-90+index*360f/count)*Math.PI/180;
                    Point p=new Point(150+(int)Math.Round(Math.Cos(angle)*74),150+(int)Math.Round(Math.Sin(angle)*74));
                    Assert(bitmap.GetPixel(p.X,p.Y).A>0,"Contiguous pie wedges must meet at their boundary: "+count+"/"+index);
                }
            }
            bitmap.Save(Path.Combine(output,"sectors-"+count+".png"));
        }
        // Verify each geometric slice has the same angular extent and no overlap.
        MethodInfo sector=typeof(TaskWheel).GetMethod("Sector",BindingFlags.NonPublic|BindingFlags.Static);
        float span=360f/count,gap=0;
        for(int index=0;index<count;index++)using(GraphicsPath p=(GraphicsPath)sector.Invoke(null,new object[]{-90+index*span+gap/2,span-gap,count}))
        {
            double middle=(-90+(index+0.5)*span)*Math.PI/180;
            Assert(p.IsVisible(150+(float)Math.Cos(middle)*90,150+(float)Math.Sin(middle)*90),"Own sector center is missing");
            if(count>1)Assert(!p.IsVisible(150-(float)Math.Cos(middle)*90,150-(float)Math.Sin(middle)*90),"Sector crosses another task");
        }
        // Outer radius 117 is isolated from the ball radius 106. No unfinished track.
        foreach(LinkedTask task in tasks) { task.status="idle";task.understanding=null; }
        wheel.Apply(tasks);
        using(Bitmap bitmap=new Bitmap(300,300))
        {
            using(Graphics g=Graphics.FromImage(bitmap))Call(wheel,"Draw",g);
            for(int degree=0;degree<360;degree+=5)
            {
                double a=degree*Math.PI/180;Assert(bitmap.GetPixel(150+(int)Math.Round(117*Math.Cos(a)),150+(int)Math.Round(117*Math.Sin(a))).A==0,"Inactive arc must have no white or colored track");
            }
        }
        // A known 50% milestone uses half of this sector's outside arc, not half a full circle.
        tasks[0].status="running";tasks[0].totalSteps=2;tasks[0].completedSteps=1;progress[tasks[0].id]=0.5f;
        using(Bitmap bitmap=new Bitmap(300,300))
        {
            using(Graphics g=Graphics.FromImage(bitmap))Call(wheel,"Draw",g);
            float start=-90+gap/2,sweep=span-gap;
            foreach(float fraction in new[]{0.25f,0.75f})
            {
                double a=(start+sweep*fraction)*Math.PI/180;Color p=bitmap.GetPixel(150+(int)Math.Round(117*Math.Cos(a)),150+(int)Math.Round(117*Math.Sin(a)));
                Assert(fraction<0.5f?p.A>180:p.A==0,"Per-sector milestone arc length is incorrect");
            }
        }
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--watch-window") { DesktopBridge.Watch(args[1]);return 0; }
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string output=args[0];Directory.CreateDirectory(output);
        try
        {
            for(int i=1;i<=5;i++)Make(i);
            using(MultiIndicatorContext context=new MultiIndicatorContext())
            {
                var initial=SessionHub.Selection();Assert(initial.selected.Count==0,"Startup must not select a historical task");
                Call(context,"Refresh");Assert(SessionHub.Selection().selected.Count==0,"Empty startup must stay taskless without user selection");
                Assert(((Dictionary<string,IndicatorForm>)Field(context,"windows")).Count==0,"Empty startup created a named task bar");
                Assert(Field(context,"idle")!=null,"Empty startup should keep the generic idle indicator available");
                var idle=(IndicatorForm)Field(context,"idle");
                Assert(Field(idle,"linkedTask")==null,"Idle indicator must not identify a historical task");
                using(Bitmap bitmap=new Bitmap(idle.Width,idle.Height)) { using(Graphics g=Graphics.FromImage(bitmap))Call(idle,"DrawIndicator",g);bitmap.Save(Path.Combine(output,"startup-none.png")); }
                Make(1);Call(context,"Refresh");
                Assert(SessionHub.Selection().selected.SequenceEqual(new[]{"fixture-1"}),"A fresh report must populate the empty indicator automatically");
                SessionHub.SaveJson("selection.json",new TaskSelection { selected=new List<string>{"fixture-1"},manual=true });
                Call(context,"Refresh");Assert(SessionHub.Selection().selected.SequenceEqual(new[]{"fixture-1"}),"Explicit first task selection failed");
                SessionHub.SaveJson("selection.json",new TaskSelection { selected=new List<string>{"fixture-2","fixture-5"},dismissed=new List<string>{"fixture-1","fixture-3","fixture-4"},manual=true });
                Call(context,"Refresh");Assert(SessionHub.Selection().selected.SequenceEqual(new[]{"fixture-2","fixture-5"}),"Manual task replacement failed");
                Call(context,"SetGrouped",true);
                var wheel=(TaskWheel)Field(context,"wheel");Assert(wheel!=null&&wheel.Visible,"Grouped ball did not appear");
                Assert(wheel.Width==150&&wheel.Height==150,"Grouped ball should use the reduced 150px canvas");
                Assert(((Dictionary<string,IndicatorForm>)Field(context,"windows")).Count==0,"Bars must be disposed in grouped mode");
                Assert(wheel.TopMost&&wheel.FormBorderStyle==FormBorderStyle.None&&!wheel.ShowInTaskbar,"Grouped window chrome/topmost regression");
                foreach(int count in new[]{1,2,3,4})DrawAndCheck(wheel,count,output);
                MethodInfo clamp=typeof(TaskWheel).GetMethod("Clamp",BindingFlags.Static|BindingFlags.NonPublic);
                foreach(Rectangle area in new[]{new Rectangle(0,40,1920,1040),new Rectangle(-1920,-1080,1920,1040)})
                foreach(Point point in new[]{new Point(-100000,-100000),new Point(100000,100000)})
                    Assert(area.Contains(new Rectangle((Point)clamp.Invoke(null,new object[]{point,wheel.Size,area}),wheel.Size)),"Wheel can escape screen bounds");
                Call(context,"SetGrouped",false);Assert(Field(context,"wheel")==null,"Expanding left a hidden wheel timer");
                var bars=(Dictionary<string,IndicatorForm>)Field(context,"windows");Assert(bars.Count==2,"Expanding did not restore selected bars");
                var barPositions=bars.Values.ToDictionary(bar=>bar,bar=>bar.Location);
                IndicatorForm source=barPositions.Keys.First();
                Point groupDelta=(Point)Call(context,"MoveBarsTogether",source,new Point(18,12));
                foreach(var pair in barPositions)Assert(pair.Key.Location==new Point(pair.Value.X+groupDelta.X,pair.Value.Y+groupDelta.Y),"Bars must move together with one shared offset");
                foreach(IndicatorForm bar in bars.Values)
                {
                    var task=(LinkedTask)Field(bar,"linkedTask");if(task.id=="fixture-2")
                    {
                        typeof(IndicatorForm).GetField("displayedScore",Hidden).SetValue(bar,53f);
                        typeof(IndicatorForm).GetField("updatingUntil",Hidden).SetValue(bar,DateTime.MinValue);
                        typeof(IndicatorForm).GetField("generationReveal",Hidden).SetValue(bar,1f);
                        typeof(IndicatorForm).GetField("taskFraction",Hidden).SetValue(bar,2f/6f);
                        using(Bitmap bitmap=new Bitmap(bar.Width,bar.Height)) { using(Graphics g=Graphics.FromImage(bitmap))Call(bar,"DrawIndicator",g);bitmap.Save(Path.Combine(output,"bar.png")); }
                    }
                }
                Call(context,"ExitThreadCore");
            }
            RenderColorExamples(output);
            // The reader interacts only with this controlled fixture window, never a user's AI client.
            using(Form fixture=new Form { Text="More Accurate accessibility fixture",Size=new Size(530,180),StartPosition=FormStartPosition.Manual,Location=new Point(50,50) })
            using(Label markerLabel=new Label { AutoSize=true,Left=12,Top=28 })
            using(Timer poll=new Timer { Interval=100 })
            {
                fixture.Controls.Add(markerLabel);Process observer=null;Exception failure=null;DateTime began=DateTime.UtcNow;
                fixture.Shown+=(s,e)=> {
                    LinkedTask task=SessionHub.Update("desktop-fixture","Claude","desktop",t=> { t.windowHandle=fixture.Handle.ToInt64();t.processId=Process.GetCurrentProcess().Id;t.processStarted=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; });
                    markerLabel.Text="[[MA:"+task.nonce+":U=67;P=running;C=2;T=5]]";
                    observer=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--watch-window desktop-fixture") { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden });poll.Start();
                };
                poll.Tick+=(s,e)=> { try { LinkedTask task=SessionHub.Read().Find(t=>t.id=="desktop-fixture");if(task!=null&&task.understanding==67) { Assert(task.completedSteps==2&&task.totalSteps==5,"Desktop reader lost milestone data");fixture.Close(); }else if((DateTime.UtcNow-began).TotalSeconds>10)throw new Exception("Selected-window UIA marker was not read"); }catch(Exception error) { failure=error;fixture.Close(); } };
                Application.Run(fixture);poll.Stop();
                if(observer!=null) { if(!observer.WaitForExit(3000))observer.Kill();observer.Dispose(); }
                if(failure!=null)throw failure;
            }
            Console.WriteLine("PASS: manual four-task selection; grouped lifecycle; 1/2/3/4 equal contiguous pie sectors; independent outer arcs; screen bounds; selected-window UIA fixture. Previews: "+output);return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
