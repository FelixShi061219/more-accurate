using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal static class NativeBridge
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    private static string Value(IDictionary<string,object> data,string key)
    { object value;return data.TryGetValue(key,out value)?Convert.ToString(value):""; }
    private static byte[] ReadExact(Stream stream,int count)
    { byte[] bytes=new byte[count];int offset=0,read;while(offset<count && (read=stream.Read(bytes,offset,count-offset))>0)offset+=read;return offset==count?bytes:null; }
    internal static void Run()
    {
        Stream input=Console.OpenStandardInput(), output=Console.OpenStandardOutput();byte[] header;
        while((header=ReadExact(input,4))!=null)
        {
            int length=BitConverter.ToInt32(header,0);if(length<=0 || length>262144)return;
            byte[] bytes=ReadExact(input,length);if(bytes==null)return;
            object result;
            try
            {
                var data=Json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(bytes));
                string action=Value(data,"action"), id=Value(data,"id"), provider=SessionHub.Provider(Value(data,"provider"));
                if(!id.StartsWith("web-",StringComparison.Ordinal) || !SessionHub.ValidId(id))throw new ArgumentException("Invalid web task identity.");
                if(action=="open" || action=="begin")
                {
                    LinkedTask task=SessionHub.Update(id,provider,"web",t=> { t.understanding=null;t.status="idle";t.completedSteps=null;t.totalSteps=null;t.lastMarker="";t.note="Waiting for this request's AI report."; });
                    result=new { ok=true,id=id,nonce=task.nonce,instruction=SessionHub.Instruction(task),name=SessionHub.Name(task,false) };
                }
                else if(action=="marker")
                {
                    LinkedTask task=SessionHub.Read().Find(t=>t.id==id);
                    if(task==null || task.provider!=provider || task.origin!="web")throw new ArgumentException("Open this task first.");
                    result=new { ok=true,updated=SessionHub.ApplyMarker(id,Value(data,"text")) };
                }
                else if(action=="close")
                {
                    SessionHub.Update(id,provider,"web",t=> { t.understanding=null;t.status="interrupted";t.note="Browser tab closed."; });
                    result=new { ok=true };
                }
                else throw new ArgumentException("Unknown action.");
            }
            catch(Exception error) { result=new { ok=false,error=error.Message }; }
            byte[] response=Encoding.UTF8.GetBytes(Json.Serialize(result));output.Write(BitConverter.GetBytes(response.Length),0,4);output.Write(response,0,response.Length);output.Flush();
        }
    }
}
