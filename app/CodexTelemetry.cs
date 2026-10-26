using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class TelemetrySnapshot
{
    public string model = "None", sessionId = "", generation = "idle", network = "None";
    public double? requestLatencyMs;
    public string latencySource = "None";
    public DateTime activityAt = DateTime.MinValue, networkAt = DateTime.MinValue;
    public bool receiverReady;
    public string receiverError = "";
    public TelemetrySnapshot Copy() { return (TelemetrySnapshot)MemberwiseClone(); }
}

// A local observer only: never forwards API traffic or retains prompts, tokens or tool output.
internal sealed class CodexTelemetry : IDisposable
{
    internal const int Port = 4319;
    private readonly object gate = new object();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    private TelemetrySnapshot current = new TelemetrySnapshot();
    private volatile bool stopped;
    private TcpListener listener;
    private readonly int listenPort;
    internal int BoundPort;
    private readonly string codexHome;
    private string sessionPath = "";
    private long offset;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private DateTime nextDiscovery = DateTime.MinValue, modelAt = DateTime.MinValue;
    private DateTime lifecycleAt = DateTime.MinValue, requestAt = DateTime.MinValue;

    internal CodexTelemetry(string home, int port = Port) { codexHome = home; listenPort = port; }
    internal TelemetrySnapshot Snapshot()
    {
        lock (gate)
        {
            TelemetrySnapshot copy = current.Copy();
            if ((DateTime.UtcNow - copy.networkAt).TotalSeconds > 90)
            { copy.network = "None"; copy.requestLatencyMs = null; copy.latencySource = "None"; }
            return copy;
        }
    }
    internal void Start()
    {
        Thread observer = new Thread(delegate() {
            while (!stopped)
            {
                try { PollSessions(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                for (int i=0;i<10 && !stopped;i++) Thread.Sleep(100);
            }
        });
        observer.IsBackground = true; observer.Start();
        Thread receiver = new Thread(ReceiveLoop); receiver.IsBackground = true; receiver.Start();
    }

    private static object Get(IDictionary<string, object> map, string name)
    { object value; return map != null && map.TryGetValue(name, out value) ? value : null; }
    private static string Text(IDictionary<string, object> map, string name)
    { return Convert.ToString(Get(map,name), CultureInfo.InvariantCulture); }
    private static double? Number(IDictionary<string, object> map, string name)
    {
        double value;
        return Double.TryParse(Text(map,name), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 ? (double?)value : null;
    }
    private static DateTime Time(string text)
    {
        DateTime result;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result) ? result : DateTime.MinValue;
    }
    private void SelectSession(string id, DateTime at)
    {
        if (String.IsNullOrEmpty(id) || current.sessionId == id) return;
        if (at < current.activityAt) return;
        bool ready = current.receiverReady; string error = current.receiverError;
        current = new TelemetrySnapshot { sessionId=id, activityAt=at, receiverReady=ready, receiverError=error };
        modelAt = lifecycleAt = requestAt = DateTime.MinValue;
    }

    internal void ConsumeSessionLine(string line)
    {
        // Ignore message bodies before deserialization. Only metadata/lifecycle/usage events matter.
        if (line.IndexOf("turn_context",StringComparison.Ordinal)<0
            && line.IndexOf("task_started",StringComparison.Ordinal)<0 && line.IndexOf("task_complete",StringComparison.Ordinal)<0
            && line.IndexOf("task_cancelled",StringComparison.Ordinal)<0 && line.IndexOf("turn_aborted",StringComparison.Ordinal)<0
            && line.IndexOf("token_count",StringComparison.Ordinal)<0) return;
        IDictionary<string, object> root;
        try { root = json.DeserializeObject(line) as IDictionary<string, object>; }
        catch (ArgumentException) { return; }
        IDictionary<string, object> payload = Get(root,"payload") as IDictionary<string, object>;
        if (payload == null) return;
        DateTime at = Time(Text(root,"timestamp"));
        if (at == DateTime.MinValue) return;
        lock (gate)
        {
            string type = Text(root,"type"), kind = Text(payload,"type");
            if (type == "turn_context")
            {
                string model = Text(payload,"model");
                if (at >= modelAt && !String.IsNullOrWhiteSpace(model) && model.Length <= 100)
                { current.model = model; modelAt = at; }
            }
            else if (type == "event_msg")
            {
                if (kind == "task_started" && at >= lifecycleAt)
                {
                    current.generation = "running"; lifecycleAt = at;
                    current.requestLatencyMs = null; current.latencySource = "None";
                    current.network = "None"; current.networkAt = DateTime.MinValue;
                }
                else if ((kind == "task_complete" || kind == "task_cancelled" || kind == "turn_aborted") && at >= lifecycleAt)
                { current.generation = kind == "task_complete" ? "complete" : "interrupted"; lifecycleAt = at; }
                // A real usage event proves a recent response; old confidence files do not.
                if (kind == "token_count" && Get(payload,"info") != null && at > current.networkAt)
                { current.network = "有响应"; current.networkAt = at; }
            }
            if (at > current.activityAt) current.activityAt = at;
        }
    }

    private void PollSessions()
    {
        DateTime now = DateTime.UtcNow;
        if (now >= nextDiscovery)
        {
            nextDiscovery = now.AddSeconds(3);
            string newest = sessionPath;
            DateTime modified = File.Exists(newest) ? File.GetLastWriteTimeUtc(newest) : DateTime.MinValue;
            // Only recent session folders, never archived history or configuration/authentication files.
            for (int days=0;days<2;days++)
            {
                string folder=Path.Combine(codexHome,"sessions",DateTime.Now.AddDays(-days).ToString("yyyy\\MM\\dd",CultureInfo.InvariantCulture));
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder,"rollout-*.jsonl"))
                {
                    DateTime candidate=File.GetLastWriteTimeUtc(file);
                    if (candidate > modified) { modified=candidate; newest=file; }
                }
            }
            if (modified < startedAt) return; // Do not replay stale sessions after login.
            if (newest != sessionPath)
            {
                sessionPath=newest; offset=0;
                string name=Path.GetFileNameWithoutExtension(newest);
                string id=name.Length>=36 ? name.Substring(name.Length-36) : "";
                lock(gate) SelectSession(id,modified);
            }
        }
        if (String.IsNullOrEmpty(sessionPath) || !File.Exists(sessionPath)) return;
        string selectedName=Path.GetFileNameWithoutExtension(sessionPath);
        string selectedId=selectedName.Length>=36?selectedName.Substring(selectedName.Length-36):"";
        lock(gate) if(current.sessionId!=selectedId) return;
        using (FileStream stream = new FileStream(sessionPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length < offset) { offset=0; }
            if (offset==0 && stream.Length>4*1024*1024)
            {
                stream.Seek(-4*1024*1024,SeekOrigin.End);
                // Discard a partial first line without decoding a partial UTF-8 sequence.
                int b; while ((b=stream.ReadByte())>=0 && b!=10) { }
            }
            else stream.Seek(offset,SeekOrigin.Begin);
            using (MemoryStream chunk = new MemoryStream())
            {
                byte[] buffer=new byte[8192]; int read, budget=4*1024*1024;
                while (budget>0 && (read=stream.Read(buffer,0,Math.Min(buffer.Length,budget)))>0)
                { chunk.Write(buffer,0,read); budget-=read; }
                // Only consume complete newline-terminated records; revisit incomplete UTF-8 later.
                byte[] bytes=chunk.ToArray(); int last=Array.LastIndexOf(bytes,(byte)10);
                long start=stream.Position-bytes.Length;
                if (last<0) return;
                offset=start+last+1;
                string text=Encoding.UTF8.GetString(bytes,0,last+1);
                using (StringReader lines=new StringReader(text))
                { string line; while((line=lines.ReadLine())!=null) ConsumeSessionLine(line); }
            }
        }
    }

    internal void ConsumeOtel(IDictionary<string, object> attributes)
    {
        string name=Text(attributes,"event.name"), id=Text(attributes,"conversation.id");
        if (!name.StartsWith("codex.",StringComparison.Ordinal) || String.IsNullOrEmpty(id)) return;
        DateTime at=Time(Text(attributes,"event.timestamp"));
        if (at==DateTime.MinValue || at>DateTime.UtcNow.AddMinutes(2)) return;
        lock(gate)
        {
            SelectSession(id,at);
            if (current.sessionId!=id) return; // Batched events from an older chat must not replace the active one.
            string model=Text(attributes,"model");
            if (at>=modelAt && !String.IsNullOrWhiteSpace(model) && model.Length<=100) { current.model=model; modelAt=at; }
            if (name=="codex.user_prompt" && at>=lifecycleAt)
            { current.generation="running"; lifecycleAt=at; current.requestLatencyMs=null; current.latencySource="None"; }
            if ((name=="codex.api_request" || name=="codex.websocket_request") && at>=requestAt)
            {
                requestAt=at;
                double? status=Number(attributes,"http.response.status_code");
                bool failure=(status.HasValue && status.Value>=400) || Text(attributes,"success")=="false" || !String.IsNullOrEmpty(Text(attributes,"error"));
                // Missing success/status cannot prove a successful connection.
                bool success=(status.HasValue && status.Value>=200 && status.Value<400) || Text(attributes,"success")=="true";
                current.network=failure?"请求失败":success?"有响应":"None";
                current.networkAt=at;
                current.requestLatencyMs=Number(attributes,"duration_ms");
                current.latencySource=name=="codex.api_request"?"HTTP 请求耗时":"WebSocket 请求耗时";
            }
            if (name=="codex.sse_event" || name=="codex.websocket_event")
            {
                string kind=Text(attributes,"event.kind");
                if ((kind=="response.failed" || Text(attributes,"success")=="false") && at>=current.networkAt)
                { current.network="请求失败"; current.networkAt=at; }
                else if (kind.StartsWith("response.",StringComparison.Ordinal) && at>current.networkAt)
                { current.network="有响应"; current.networkAt=at; }
                // response.completed may precede tool use; only task_complete completes the whole answer.
            }
            if (at>current.activityAt) current.activityAt=at;
        }
    }

    private void ReceiveLoop()
    {
        try
        {
            listener=new TcpListener(IPAddress.Loopback,listenPort); listener.Start(8);
            BoundPort=((IPEndPoint)listener.LocalEndpoint).Port;
            lock(gate) current.receiverReady=true;
            while(!stopped)
            {
                using(TcpClient client=listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout=2000; client.SendTimeout=2000;
                    try { Receive(client.GetStream()); }
                    catch(IOException) { }
                    catch(ArgumentException) { }
                    catch(InvalidDataException) { }
                    catch(FormatException) { }
                }
            }
        }
        catch(SocketException) { lock(gate) current.receiverError="本机遥测端口被占用"; }
        catch(ObjectDisposedException) { }
        finally { lock(gate) current.receiverReady=false; }
    }
    private void Receive(NetworkStream stream)
    {
        const int limit=4*1024*1024;
        List<byte> header=new List<byte>(); int value;
        while(header.Count<16384 && (value=stream.ReadByte())>=0)
        {
            header.Add((byte)value); int c=header.Count;
            if(c>=4 && header[c-4]==13 && header[c-3]==10 && header[c-2]==13 && header[c-1]==10) break;
        }
        string headers=Encoding.ASCII.GetString(header.ToArray());
        int length=0; bool gzip=false, valid=false, protobuf=false;
        string[] lines=headers.Split(new[]{"\r\n"},StringSplitOptions.None);
        if(lines.Length>0) valid=lines[0]=="POST /v1/logs HTTP/1.1" || lines[0]=="POST /v1/logs HTTP/1.0";
        foreach(string line in lines)
        {
            if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) Int32.TryParse(line.Substring(15).Trim(),out length);
            if(line.StartsWith("Content-Encoding:",StringComparison.OrdinalIgnoreCase)) gzip=line.ToLowerInvariant().Contains("gzip");
            if(line.StartsWith("Content-Type:",StringComparison.OrdinalIgnoreCase)) protobuf=line.ToLowerInvariant().Contains("application/x-protobuf");
        }
        if(!valid || !protobuf || length<=0 || length>limit) { Reply(stream,400); return; }
        byte[] body=new byte[length]; int offset=0, n;
        while(offset<body.Length && (n=stream.Read(body,offset,body.Length-offset))>0) offset+=n;
        if(offset!=body.Length) { Reply(stream,400); return; }
        if(gzip)
        {
            using(MemoryStream source=new MemoryStream(body))
            using(GZipStream unpack=new GZipStream(source,CompressionMode.Decompress))
            using(MemoryStream target=new MemoryStream())
            {
                byte[] buffer=new byte[8192];
                while((n=unpack.Read(buffer,0,buffer.Length))>0) { if(target.Length+n>limit) throw new InvalidDataException(); target.Write(buffer,0,n); }
                body=target.ToArray();
            }
        }
        foreach(ProtoField resource in ProtoField.Read(body)) if(resource.number==1 && resource.bytes!=null)
        foreach(ProtoField scope in ProtoField.Read(resource.bytes)) if(scope.number==2 && scope.bytes!=null)
        foreach(ProtoField record in ProtoField.Read(scope.bytes)) if(record.number==2 && record.bytes!=null)
        {
            Dictionary<string,object> attributes=new Dictionary<string,object>();
            foreach(ProtoField field in ProtoField.Read(record.bytes)) if(field.number==6 && field.bytes!=null)
            {
                string key=""; object entry=null;
                foreach(ProtoField kv in ProtoField.Read(field.bytes))
                {
                    if(kv.number==1 && kv.bytes!=null) key=Encoding.UTF8.GetString(kv.bytes);
                    if(kv.number==2 && kv.bytes!=null) entry=ProtoField.Any(kv.bytes);
                }
                // Whitelist metrics only; never retain arbitrary telemetry payload/body.
                if(key=="event.name" || key=="conversation.id" || key=="event.timestamp" || key=="model" || key=="event.kind" || key=="duration_ms" || key=="http.response.status_code" || key=="success") attributes[key]=entry;
            }
            ConsumeOtel(attributes);
        }
        Reply(stream,200);
    }
    private static void Reply(Stream stream,int status)
    {
        byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+(status==200?" OK":" Bad Request")+"\r\nContent-Type: application/x-protobuf\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        stream.Write(response,0,response.Length);
    }
    public void Dispose() { stopped=true; if(listener!=null) listener.Stop(); }
}

internal sealed class ProtoField
{
    internal int number,wire; internal ulong integer; internal byte[] bytes;
    private static ulong Varint(byte[] source,ref int at)
    {
        ulong result=0;
        for(int shift=0;shift<64 && at<source.Length;shift+=7)
        { byte b=source[at++]; if(shift==63 && b>1) throw new InvalidDataException(); result|=(ulong)(b&127)<<shift; if((b&128)==0) return result; }
        throw new InvalidDataException();
    }
    internal static IEnumerable<ProtoField> Read(byte[] source)
    {
        int at=0;
        while(at<source.Length)
        {
            ulong tag=Varint(source,ref at); ProtoField field=new ProtoField { number=(int)(tag>>3),wire=(int)(tag&7) };
            if(field.number==0) throw new InvalidDataException();
            if(field.wire==0) field.integer=Varint(source,ref at);
            else
            {
                ulong size=field.wire==2?Varint(source,ref at):field.wire==1?8UL:field.wire==5?4UL:UInt64.MaxValue;
                if(size>(ulong)(source.Length-at)) throw new InvalidDataException();
                field.bytes=new byte[(int)size]; Buffer.BlockCopy(source,at,field.bytes,0,(int)size); at+=(int)size;
            }
            yield return field;
        }
    }
    internal static object Any(byte[] source)
    {
        foreach(ProtoField f in Read(source))
        {
            if(f.number==1 && f.bytes!=null) return Encoding.UTF8.GetString(f.bytes);
            if(f.number==2) return f.integer!=0?"true":"false";
            if(f.number==3) return unchecked((long)f.integer);
            if(f.number==4 && f.bytes!=null && f.bytes.Length==8) return BitConverter.ToDouble(f.bytes,0);
        }
        return null;
    }
}
