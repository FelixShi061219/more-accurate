using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class TelemetryCheck
{
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    static string Stamp(DateTime at) { return at.ToString("o"); }
    static string Line(DateTime at,string type,object payload) { return Json.Serialize(new { timestamp=Stamp(at),type=type,payload=payload }); }
    static Dictionary<string,object> Event(DateTime at,string name,string session)
    { return new Dictionary<string,object>{{"event.name",name},{"event.timestamp",Stamp(at)},{"conversation.id",session},{"model","gpt-test"}}; }
    static byte[] V(ulong value) { List<byte> b=new List<byte>(); do { byte part=(byte)(value&127); value>>=7; b.Add((byte)(part | (value>0?128:0))); } while(value>0); return b.ToArray(); }
    static byte[] Join(params byte[][] parts) { using(MemoryStream b=new MemoryStream()) { foreach(byte[] p in parts)b.Write(p,0,p.Length); return b.ToArray(); } }
    static byte[] F(int key,byte[] body) { return Join(V((ulong)((key<<3)|2)),V((ulong)body.Length),body); }
    static byte[] S(int key,string value) { return F(key,Encoding.UTF8.GetBytes(value)); }
    static byte[] Export(Dictionary<string,object> attrs)
    {
        List<byte[]> entries=new List<byte[]>();
        foreach(KeyValuePair<string,object> pair in attrs) entries.Add(F(6,Join(S(1,pair.Key),F(2,S(1,Convert.ToString(pair.Value,CultureInfo.InvariantCulture))))));
        return F(1,F(2,F(2,Join(entries.ToArray()))));
    }
    static void Post(int port,byte[] payload,bool gzip)
    {
        if(gzip) using(MemoryStream memory=new MemoryStream()) { using(GZipStream zip=new GZipStream(memory,CompressionMode.Compress,true))zip.Write(payload,0,payload.Length); payload=memory.ToArray(); }
        using(TcpClient client=new TcpClient("127.0.0.1",port))
        {
            client.ReceiveTimeout=3000;
            NetworkStream stream=client.GetStream();
            byte[] head=Encoding.ASCII.GetBytes("POST /v1/logs HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/x-protobuf\r\n"+(gzip?"Content-Encoding: gzip\r\n":"")+"Content-Length: "+payload.Length+"\r\n\r\n");
            stream.Write(head,0,head.Length); stream.Write(payload,0,payload.Length);
            byte[] reply=new byte[1024]; int n=stream.Read(reply,0,reply.Length);
            Check(Encoding.ASCII.GetString(reply,0,n).StartsWith("HTTP/1.1 200"),"OTLP receiver rejected valid binary data.");
        }
    }
    static int Main()
    {
        string home=Path.Combine(Path.GetTempPath(),"more-accurate-telemetry-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            DateTime now=DateTime.UtcNow;
            using(CodexTelemetry telemetry=new CodexTelemetry(home,0))
            {
                Check(telemetry.Snapshot().model=="None" && telemetry.Snapshot().network=="None" && !telemetry.Snapshot().requestLatencyMs.HasValue,"Unknown data must remain None.");
                telemetry.ConsumeSessionLine(Line(now,"event_msg",new { type="task_started" }));
                Check(telemetry.Snapshot().generation=="running","Turn start not observed.");
                telemetry.ConsumeSessionLine(Line(now,"turn_context",new { model="gpt-6.1-sol" }));
                telemetry.ConsumeSessionLine(Line(now,"event_msg",new { type="token_count", info=new { } }));
                Check(telemetry.Snapshot().model=="gpt-6.1-sol" && telemetry.Snapshot().network=="有响应","Local model/response metadata not observed.");
                Check(!telemetry.Snapshot().requestLatencyMs.HasValue,"Usage event must not invent latency.");
                telemetry.ConsumeSessionLine(Line(now.AddMilliseconds(1),"event_msg",new { type="task_complete" }));
                Check(telemetry.Snapshot().generation=="complete","Completion not observed.");
                telemetry.ConsumeSessionLine(Line(now.AddMilliseconds(-1),"event_msg",new { type="task_started" }));
                Check(telemetry.Snapshot().generation=="complete","Out-of-order event regressed generation.");
                telemetry.ConsumeSessionLine("{\"partial\":");
                // An actual request event has a real latency and HTTP outcome.
                var request=Event(now.AddMilliseconds(2),"codex.api_request","thread-a");
                request["duration_ms"]="2971"; request["http.response.status_code"]=200;
                telemetry.ConsumeOtel(request);
                Check(telemetry.Snapshot().requestLatencyMs==2971 && telemetry.Snapshot().network=="有响应","Request metric parse failed.");
                var streamDone=Event(now.AddMilliseconds(3),"codex.sse_event","thread-a"); streamDone["event.kind"]="response.completed";
                telemetry.ConsumeOtel(streamDone);
                Check(telemetry.Snapshot().generation!="complete","One model response cannot complete an entire tool-using turn.");
                var prompt=Event(now.AddMilliseconds(4),"codex.user_prompt","thread-a"); telemetry.ConsumeOtel(prompt);
                Check(telemetry.Snapshot().generation=="running" && !telemetry.Snapshot().requestLatencyMs.HasValue,"New turn did not reset request latency.");
                var failure=Event(now.AddMilliseconds(5),"codex.api_request","thread-a"); failure["http.response.status_code"]=503; failure["duration_ms"]="123"; telemetry.ConsumeOtel(failure);
                Check(telemetry.Snapshot().network=="请求失败","HTTP failure not shown.");
                var next=Event(now.AddMilliseconds(6),"codex.user_prompt","thread-b"); next["model"]="different-model";telemetry.ConsumeOtel(next);
                telemetry.ConsumeOtel(request);
                Check(telemetry.Snapshot().sessionId=="thread-b" && telemetry.Snapshot().model=="different-model" && !telemetry.Snapshot().requestLatencyMs.HasValue,"Old batched chat events mixed with active chat.");
                telemetry.Start();
                for(int i=0;i<50 && !telemetry.Snapshot().receiverReady;i++)Thread.Sleep(20);
                Check(telemetry.Snapshot().receiverReady,"Receiver not ready.");
                var actual=Event(now.AddMilliseconds(7),"codex.api_request","thread-b"); actual["http.response.status_code"]=200; actual["duration_ms"]="81.5";
                actual["prompt"]="PRIVATE TEST CONTENT MUST NOT BE RETAINED";
                Post(telemetry.BoundPort,Export(actual),false);
                Check(telemetry.Snapshot().requestLatencyMs==81.5,"Real OTLP HTTP protobuf route failed.");
                actual["event.timestamp"]=Stamp(now.AddMilliseconds(8)); actual["duration_ms"]="92";
                Post(telemetry.BoundPort,Export(actual),true);
                Check(telemetry.Snapshot().requestLatencyMs==92,"Compressed OTLP data failed.");
            }
            using(CodexTelemetry stale=new CodexTelemetry(home,0))
            {
                var old=Event(now.AddMinutes(-5),"codex.api_request","old"); old["http.response.status_code"]=200; old["duration_ms"]=100;
                stale.ConsumeOtel(old);
                Check(stale.Snapshot().network=="None" && !stale.Snapshot().requestLatencyMs.HasValue,"Stale response must not claim current connectivity or latency.");
            }
            Console.WriteLine("PASS: model/lifecycle; unknown=None; real HTTP and gzip/protobuf OTLP; request success/failure/latency; no stale connection; no cross-session metrics; no model-response/turn-complete confusion.");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(home); }
    }
}
