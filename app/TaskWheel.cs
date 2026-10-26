using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

internal sealed class TaskWheel : Form
{
    private const int Canvas=300, WindowCanvas=150, Center=150;
    private const float DisplayScale=0.5f;
    private const float Radius=106, InnerRadius=9, OuterRadius=117;
    private const int BallBox=44, BallSize=212, ArcBox=33, ArcSize=234;
    private List<LinkedTask> tasks=new List<LinkedTask>();
    private readonly Dictionary<string,float> levels=new Dictionary<string,float>();
    private readonly Dictionary<string,float> progress=new Dictionary<string,float>();
    private readonly Dictionary<string,DateTime> updating=new Dictionary<string,DateTime>();
    private readonly System.Windows.Forms.Timer animation=new System.Windows.Forms.Timer();
    private readonly ToolTip hint=new ToolTip();
    private readonly DateTime startedAt=System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private DateTime lastFrame=DateTime.UtcNow,nextTopmost=DateTime.MinValue;
    private float flow,tilt,velocity,energy,shape=0.8f,shapeVelocity;
    private Point offset;
    private bool dragging;
    private readonly List<Form> menuHiddenForms=new List<Form>();
    private string language="en";
    private IndicatorPreferences preferences=new IndicatorPreferences();
    internal Action Expand,ManageTasks,LinkDesktop,ExitAll;
    internal TaskWheel()
    {
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;TopMost=true;
        ClientSize=new Size(WindowCanvas,WindowCanvas);Text="More Accurate";AccessibleName="More Accurate task sectors";
        Location=new Point(Screen.PrimaryScreen.WorkingArea.Left+100,Screen.PrimaryScreen.WorkingArea.Top+100);
        try { string global=Path.Combine(SessionHub.Root,"preferences.json");if(File.Exists(global))preferences=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<IndicatorPreferences>(File.ReadAllText(global))??preferences;
            language=preferences.language;
            string path=Path.Combine(SessionHub.Root,"wheel-position.json");if(File.Exists(path)) {
            var saved=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<IndicatorPreferences>(File.ReadAllText(path));
            if(saved!=null && preferences.rememberPosition)Location=new Point(saved.x,saved.y);
        }}catch(IOException) { }catch(ArgumentException) { }
        KeepInside();BuildMenu();
        MouseDown+=(s,e)=> { if(e.Button==MouseButtons.Left) { dragging=true;Capture=true;offset=e.Location; } };
        MouseMove+=(s,e)=> {
            if(dragging) { Point p=PointToScreen(e.Location);Point next=Clamp(new Point(p.X-offset.X,p.Y-offset.Y),Size,Screen.FromPoint(p).WorkingArea);
                velocity=Math.Max(-100,Math.Min(100,velocity-(next.X-Left)*1.5f));energy=Math.Min(7,energy+Math.Abs(next.Y-Top)*0.15f+Math.Abs(next.X-Left)*0.1f);Location=next; }
            else { int index=HitSector(e.Location);if(index>=0)hint.SetToolTip(this,SessionHub.Name(tasks[index],language=="en")+"\n"+(tasks[index].note??"")); }
        };
        MouseUp+=(s,e)=> { dragging=false;Capture=false;SavePosition(); };
        MouseCaptureChanged+=(s,e)=> { if(!Capture)dragging=false; };
        MouseDoubleClick+=(s,e)=> { if(e.Button==MouseButtons.Left && Expand!=null)Expand(); };
        animation.Interval=16;animation.Tick+=(s,e)=> {
            DateTime now=DateTime.UtcNow;float dt=(float)Math.Max(0,Math.Min(0.08,(now-lastFrame).TotalSeconds));lastFrame=now;
            flow=(flow+dt*1.2f)%6.283185f;energy*=(float)Math.Exp(-dt*1.8f);
            float remaining=dt;while(remaining>0) { float step=Math.Min(0.008f,remaining);velocity+=(-tilt*42-velocity*4.6f)*step;tilt=Math.Max(-9,Math.Min(9,tilt+velocity*step));shapeVelocity+=((1-shape)*260-shapeVelocity*19)*step;shape+=shapeVelocity*step;remaining-=step; }
            foreach(LinkedTask task in tasks) {
                if(!levels.ContainsKey(task.id))levels[task.id]=0;
                float target=Fresh(task)&&task.understanding.HasValue?task.understanding.Value:0;
                levels[task.id]+=(target-levels[task.id])*(1-(float)Math.Exp(-dt*11));
                if(!progress.ContainsKey(task.id))progress[task.id]=0;
                float fraction=task.totalSteps.HasValue&&task.completedSteps.HasValue&&task.totalSteps>0?(float)task.completedSteps.Value/task.totalSteps.Value:0;
                progress[task.id]+=(fraction-progress[task.id])*(1-(float)Math.Exp(-dt*12));
            }
            if(now>=nextTopmost && !ContextMenuStrip.Visible) { LayeredIndicator.KeepOnTop(Handle);nextTopmost=now.AddSeconds(1); }
            Render();
        };
        Shown+=(s,e)=> { KeepInside();lastFrame=DateTime.UtcNow;Render();animation.Start(); };
    }
    private static Point Clamp(Point p,Size size,Rectangle area) { return new Point(Math.Max(area.Left,Math.Min(area.Right-size.Width,p.X)),Math.Max(area.Top,Math.Min(area.Bottom-size.Height,p.Y))); }
    private void KeepInside() { Location=Clamp(Location,Size,Screen.FromRectangle(Bounds).WorkingArea); }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { CreateParams p=base.CreateParams;p.ExStyle|=0x00080000|0x08000000|0x00000080;return p; } }
    protected override void WndProc(ref Message m) { base.WndProc(ref m);if((m.Msg==0x007E||m.Msg==0x001A||m.Msg==0x02E0)&&IsHandleCreated&&!IsDisposed)BeginInvoke((MethodInvoker)delegate { if(!IsDisposed)KeepInside(); }); }
    protected override void Dispose(bool disposing) { if(disposing) { animation.Stop();animation.Dispose();hint.Dispose(); }base.Dispose(disposing); }
    private string Local(string zh,string en) { return language=="en"?en:zh; }
    private void SavePosition()
    {
        try {
            preferences.language=language;SessionHub.SaveJson("preferences.json",preferences);
            SessionHub.SaveJson("wheel-position.json",new IndicatorPreferences { x=Left,y=Top,rememberPosition=preferences.rememberPosition,language=language });
        }catch(IOException) { }
    }
    private void BuildMenu()
    {
        ContextMenuStrip old=ContextMenuStrip;ContextMenuStrip menu=new ContextMenuStrip { Font=Handwriting.Make(true,12) };
        menu.Items.Add(Local("展开进度条","Expand bars"),null,(s,e)=> { if(Expand!=null)Expand(); });
        menu.Items.Add(Local("选择任务（最多四个）","Choose tasks (up to four)"),null,(s,e)=> { if(ManageTasks!=null)ManageTasks(); });
        menu.Items.Add(Local("连接桌面 AI","Link desktop AI"),null,(s,e)=> { if(LinkDesktop!=null)LinkDesktop(); });
        ToolStripMenuItem copy=new ToolStripMenuItem(Local("复制联动指令","Copy linking instruction"));
        foreach(LinkedTask task in tasks) { LinkedTask t=task;copy.DropDownItems.Add(SessionHub.Name(t,language=="en"),null,(s,e)=>Clipboard.SetText(SessionHub.Instruction(t))); }
        menu.Items.Add(copy);
        ToolStripMenuItem languages=new ToolStripMenuItem("Language");
        languages.DropDownItems.Add("简体中文",null,(s,e)=> IndicatorForm.SetLanguageEverywhere("zh-CN"));
        languages.DropDownItems.Add("English",null,(s,e)=> IndicatorForm.SetLanguageEverywhere("en"));menu.Items.Add(languages);
        menu.Items.Add(new ToolStripMenuItem(Local("始终置顶","Always on top")) { Checked=true,Enabled=false });
        ToolStripMenuItem login=new ToolStripMenuItem(Local("开机启动","Launch at login")) { CheckOnClick=true,Checked=preferences.launchAtLogin };
        login.Click+=(s,e)=> { using(var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) { if(login.Checked)key.SetValue("MoreAccurateIndicator","\""+Application.ExecutablePath+"\"");else key.DeleteValue("MoreAccurateIndicator",false); } preferences.launchAtLogin=login.Checked;SavePosition(); };menu.Items.Add(login);
        ToolStripMenuItem remember=new ToolStripMenuItem(Local("记住位置","Remember position")) { CheckOnClick=true,Checked=preferences.rememberPosition };
        remember.Click+=(s,e)=> { preferences.rememberPosition=remember.Checked;SavePosition(); };menu.Items.Add(remember);
        menu.Items.Add(new ToolStripSeparator());menu.Items.Add(Local("退出","Exit"),null,(s,e)=> { if(ExitAll!=null)ExitAll(); });ContextMenuStrip=menu;if(old!=null)old.Dispose();
        menu.Opening+=(s,e)=> { foreach(Form form in Application.OpenForms) if(form!=this && (form is IndicatorForm || form is TaskWheel)) { menuHiddenForms.Add(form);form.Hide(); } TopMost=false; };
        menu.Closed+=(s,e)=> { foreach(Form form in menuHiddenForms.ToArray()) if(!form.IsDisposed) { form.TopMost=true;form.Show(); } menuHiddenForms.Clear();TopMost=true; };
    }
    internal void ApplyLanguageLocal(string value) { language=value=="en"?"en":"zh-CN";preferences.language=language;BuildMenu();SavePosition();Render(); }
    internal void Apply(List<LinkedTask> next)
    {
        string before=String.Join(",",tasks.Select(t=>t.id)),after=String.Join(",",next.Select(t=>t.id));
        foreach(LinkedTask task in next.Take(4)) { LinkedTask old=tasks.Find(t=>t.id==task.id);if(old==null||old.updatedAt!=task.updatedAt)updating[task.id]=DateTime.UtcNow.AddMilliseconds(600); }
        tasks=next.Take(4).ToList();
        foreach(string id in levels.Keys.Where(id=>!tasks.Any(t=>t.id==id)).ToList())levels.Remove(id);
        foreach(string id in progress.Keys.Where(id=>!tasks.Any(t=>t.id==id)).ToList())progress.Remove(id);
        foreach(string id in updating.Keys.Where(id=>!tasks.Any(t=>t.id==id)).ToList())updating.Remove(id);
        if(before!=after)BuildMenu();
    }
    private bool Fresh(LinkedTask task) { DateTime at;return DateTime.TryParse(task.updatedAt,null,System.Globalization.DateTimeStyles.RoundtripKind,out at)&&at.ToUniversalTime()>=startedAt; }
    private int HitSector(Point point)
    {
        point=new Point((int)(point.X/DisplayScale),(int)(point.Y/DisplayScale));
        if(tasks.Count==0)return -1;float dx=point.X-Center,dy=point.Y-Center;
        if(dx*dx+dy*dy>(OuterRadius+8)*(OuterRadius+8))return -1;
        double angle=(Math.Atan2(dy,dx)*180/Math.PI+90+360)%360;return Math.Min(tasks.Count-1,(int)(angle/(360f/tasks.Count)));
    }
    private static GraphicsPath Sector(float start,float sweep,int count)
    {
        GraphicsPath path=new GraphicsPath();
        if(count==1) { path.AddEllipse(BallBox,BallBox,BallSize,BallSize);return path; }
        const float radius=Radius,inner=InnerRadius;
        double angle=start*Math.PI/180;
        path.StartFigure();path.AddLine(Center+(float)Math.Cos(angle)*inner,Center+(float)Math.Sin(angle)*inner,Center+(float)Math.Cos(angle)*radius,Center+(float)Math.Sin(angle)*radius);
        path.AddArc(BallBox,BallBox,BallSize,BallSize,start,sweep);
        angle=(start+sweep)*Math.PI/180;path.AddLine(Center+(float)Math.Cos(angle)*radius,Center+(float)Math.Sin(angle)*radius,Center+(float)Math.Cos(angle)*inner,Center+(float)Math.Sin(angle)*inner);
        path.AddArc(Center-InnerRadius,Center-InnerRadius,InnerRadius*2,InnerRadius*2,start+sweep,-sweep);path.CloseFigure();return path;
    }
    private static Color[] Colors(int score)
    {
        if(score>=90)return new[]{Color.FromArgb(122,203,159),Color.FromArgb(176,231,188),Color.FromArgb(141,220,198)};
        if(score>=75)return new[]{Color.FromArgb(197,185,230),Color.FromArgb(222,211,242),Color.FromArgb(198,196,236)};
        if(score>=50)return new[]{Color.FromArgb(227,210,156),Color.FromArgb(241,229,185),Color.FromArgb(232,215,166)};
        if(score>=25)return new[]{Color.FromArgb(231,178,141),Color.FromArgb(243,210,174),Color.FromArgb(234,189,160)};
        return new[]{Color.FromArgb(229,165,173),Color.FromArgb(242,189,193),Color.FromArgb(232,170,184)};
    }
    private void Draw(Graphics g)
    {
        g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if(tasks.Count==0)return;
        // Use contiguous pie-chart wedges. Their antialiased outline is the only separator.
        float span=360f/tasks.Count,gap=0;
        for(int index=0;index<tasks.Count;index++)
        {
            LinkedTask task=tasks[index];float start=-90+index*span+gap/2,sweep=span-gap;
            float level=levels.ContainsKey(task.id)?levels[task.id]:0;bool fresh=Fresh(task),known=fresh&&task.understanding.HasValue;
            using(GraphicsPath sector=Sector(start,sweep,tasks.Count))
            {
                GraphicsState shadowState=g.Save();g.TranslateTransform(0,3);
                for(int width=5;width>=1;width--)using(Pen shadow=new Pen(Color.FromArgb(3,0,0,0),width*2))g.DrawArc(shadow,BallBox,BallBox,BallSize,BallSize,start,sweep);
                g.Restore(shadowState);
                using(Brush baseBrush=new SolidBrush(Color.FromArgb(226,229,233)))g.FillPath(baseBrush,sector);
                GraphicsState fillState=g.Save();g.SetClip(sector);
                if(known && level>0)
                {
                    // Each task's water level fills the vertical extent of its own sector.
                    RectangleF bounds=sector.GetBounds();float surface=bounds.Bottom-bounds.Height*level/100f;
                    using(GraphicsPath water=new GraphicsPath())
                    {
                        List<PointF> points=new List<PointF>();
                        for(float x=bounds.Left-2;x<=bounds.Right+4;x+=3)points.Add(new PointF(x,surface+(float)Math.Sin(x*0.055+flow)*((level>1&&level<99)?1.3f+energy*0.35f:0)+(x-Center)*tilt/120f));
                        points.Add(new PointF(bounds.Right+5,bounds.Bottom+10));points.Add(new PointF(bounds.Left-5,bounds.Bottom+10));water.AddPolygon(points.ToArray());
                        Color[] colors=Colors(task.understanding.Value);
                        using(LinearGradientBrush paint=new LinearGradientBrush(new Rectangle(45,45,250,250),colors[0],colors[2],35f)) { paint.InterpolationColors=new ColorBlend { Colors=colors,Positions=new[]{0f,0.55f,1f} };g.FillPath(paint,water); }
                    }
                }
                g.Restore(fillState);using(Pen outline=new Pen(Color.FromArgb(20,22,24),1.7f))g.DrawPath(outline,sector);
                double middle=(start+sweep/2)*Math.PI/180;float radius=tasks.Count==1?0:58;
                float xLabel=Center+(float)Math.Cos(middle)*radius,yLabel=Center+(float)Math.Sin(middle)*radius;
                using(Font providerFont=Handwriting.Make(true,tasks.Count==1?15:12))
                using(Font taskFont=Handwriting.Make(true,tasks.Count==1?12:10.5f))
                using(Font scoreFont=Handwriting.Make(true,tasks.Count==1?21:17))
                using(Brush ink=new SolidBrush(Color.FromArgb(19,23,27)))
                using(StringFormat centered=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap })
                {
                    g.DrawString(task.provider,providerFont,ink,new RectangleF(xLabel-57,yLabel-35,114,25),centered);
                    g.DrawString(Local("任务","Task ")+task.number,taskFont,ink,new RectangleF(xLabel-57,yLabel-12,114,22),centered);
                    DateTime until;
                    if(known&&updating.TryGetValue(task.id,out until)&&DateTime.UtcNow<until)
                    {
                        for(int dot=0;dot<3;dot++) { float lift=Math.Max(0,(float)Math.Sin(flow*5-dot*0.85f))*3;g.FillEllipse(ink,xLabel-13+dot*11,yLabel+25-lift,4,4); }
                    }
                    else g.DrawString(known?Math.Round(level)+"%":"None",scoreFont,ink,new RectangleF(xLabel-57,yLabel+9,114,34),centered);
                }
            }
            if(fresh&&task.status=="running")
            using(Pen arc=new Pen(Color.FromArgb(92,143,128),6) { StartCap=LineCap.Round,EndCap=LineCap.Round })
            {
                // The task's own sector arc, rather than the whole circle, is 100%.
                float fraction=progress.ContainsKey(task.id)?progress[task.id]:0;
                float length=task.totalSteps.HasValue?sweep*fraction:Math.Max(12,sweep*0.23f);
                float from=task.totalSteps.HasValue?start:start+(sweep-length)*(0.5f+0.5f*(float)Math.Sin(flow));
                if(length>0.05f)g.DrawArc(arc,ArcBox,ArcBox,ArcSize,ArcSize,from,Math.Min(sweep,length));
            }
        }
    }
    private void Render()
    {
        using(Bitmap bitmap=new Bitmap(Width,Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        {
            using(Graphics g=Graphics.FromImage(bitmap))
            {
                g.TranslateTransform(Center*DisplayScale,Center*DisplayScale);
                g.ScaleTransform(shape*DisplayScale,shape*DisplayScale);
                g.TranslateTransform(-Center,-Center);
                Draw(g);
            }
            LayeredIndicator.Present(this,bitmap,255);
        }
    }
}
