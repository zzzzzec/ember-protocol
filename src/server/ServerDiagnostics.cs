using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Common;

namespace Codex.ZombieSurvival;

internal sealed class ServerJournal
{
    private readonly ConcurrentQueue<(string File,string Line)> _queue=new();
    private readonly AutoResetEvent _signal=new(false);
    private readonly Task _writer;
    private int _dropped;
    public string Root { get; }
    public string Run { get; }
    public ServerJournal(string root,string run)
    {
        Root=root;Run=run;Directory.CreateDirectory(root);
        var day=System.IO.Path.Combine(root,DateTime.Now.ToString("yyyy-MM-dd"));Directory.CreateDirectory(day);
        _writer=Task.Factory.StartNew(WriteLoop,TaskCreationOptions.LongRunning);
        _=Task.Run(()=>Cleanup(root,30,1610612736L));
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>Flush();
    }
    public void Record(string type,object? data,string? player=null,string? raid=null,string? secret=null)
    {
        try
        {
            if(_queue.Count>2048){Interlocked.Increment(ref _dropped);return;}
            var line=JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,run=Run,source="server",type,player,raid,data,dropped=Interlocked.Exchange(ref _dropped,0)});
            if(!string.IsNullOrEmpty(secret))line=line.Replace(secret,"player:"+player,StringComparison.Ordinal);
            var name=(raid??Run)+"-server.jsonl";
            _queue.Enqueue((System.IO.Path.Combine(Root,DateTime.Now.ToString("yyyy-MM-dd"),name),line));_signal.Set();
        }
        catch { Interlocked.Increment(ref _dropped); }
    }
    private void WriteLoop()
    {
        while(true)
        {
            _signal.WaitOne(1500);
            try
            {
                var batch=new List<(string File,string Line)>();while(_queue.TryDequeue(out var row))batch.Add(row);
                foreach(var group in batch.GroupBy(x=>x.File))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(group.Key)!);
                    var file=group.Key;var part=0;while(File.Exists(file)&&new FileInfo(file).Length>33554432)file=group.Key.Replace(".jsonl","-part"+(++part)+".jsonl");
                    using var stream=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.ReadWrite);
                    using var writer=new StreamWriter(stream,new UTF8Encoding(false));
                    foreach(var row in group)writer.WriteLine(row.Line);
                }
            }
            catch {while(_queue.TryDequeue(out _)){} }
        }
    }
    public void Flush(){_signal.Set();for(var i=0;i<20&&_queue.Count>0;i++)Thread.Sleep(10);}
    internal static void Cleanup(string root,int days,long limit)
    {
        try
        {
            var full=System.IO.Path.GetFullPath(root)+System.IO.Path.DirectorySeparatorChar;
            var daysDirs=Directory.EnumerateDirectories(root).Where(p=>Regex.IsMatch(System.IO.Path.GetFileName(p),@"^\d{4}-\d{2}-\d{2}$")&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0);
            var files=daysDirs.SelectMany(p=>Directory.EnumerateFiles(p,"*.jsonl",SearchOption.TopDirectoryOnly)).Select(p=>new FileInfo(p)).OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
            var size=files.Sum(f=>f.Length);var keep=files.Take(100).Select(f=>f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var f in files.Reverse())
                if(!keep.Contains(f.FullName)&&f.FullName.StartsWith(full,StringComparison.OrdinalIgnoreCase)&&(f.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-days)||size>limit))
                {var bytes=f.Length;f.Delete();size-=bytes;}
        }
        catch { }
    }
}

public sealed partial class SurvivalService
{
    // Native request DTO converters are intended for reads and can recurse on
    // writes. Diagnostics projects plain values without invoking those writers.
    private static JsonNode? DiagnosticNode(object? value,int depth=0,HashSet<object>? seen=null)
    {
        if(value is null)return null;
        if(value is JsonNode node)return node.DeepClone();
        if(value is string text)return JsonValue.Create(text);
        var type=value.GetType();
        if(type.IsEnum||value is MongoId||value is DateTime||value is DateTimeOffset||value is Guid)return JsonValue.Create(value.ToString());
        if(type.IsPrimitive||value is decimal)return JsonSerializer.SerializeToNode(value);
        if(depth>=16)return JsonValue.Create("[depth limit: "+type.Name+"]");
        seen??=new HashSet<object>(ReferenceEqualityComparer.Instance);
        if(!type.IsValueType&&!seen.Add(value))return JsonValue.Create("[reference: "+type.Name+"]");
        try
        {
            if(value is IDictionary dictionary)
            {var result=new JsonObject();var count=0;foreach(DictionaryEntry item in dictionary){if(++count>10000)break;result[item.Key.ToString()??""]=DiagnosticNode(item.Value,depth+1,seen);}return result;}
            if(value is IEnumerable sequence)
            {var result=new JsonArray();var count=0;foreach(var item in sequence){if(++count>10000)break;result.Add(DiagnosticNode(item,depth+1,seen));}return result;}
            var obj=new JsonObject();
            foreach(var p in type.GetProperties(BindingFlags.Public|BindingFlags.Instance).Where(p=>p.CanRead&&p.GetIndexParameters().Length==0))
            {
                var name=p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name??p.Name;
                if(Regex.IsMatch(name,"password|token|authorization",RegexOptions.IgnoreCase))continue;
                try{obj[name]=DiagnosticNode(p.GetValue(value),depth+1,seen);}catch {obj[name]="[unavailable]";}
            }
            return obj;
        }
        finally{if(!type.IsValueType)seen.Remove(value);}
    }
    private ServerJournal? _journal;
    private readonly ConcurrentDictionary<string,string> _raidIds=new();
    private static string PlayerKey(string raw)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    private string DiagnosticRoot()
    {
        var d=new DirectoryInfo(Folder);
        while(d is not null){if(File.Exists(System.IO.Path.Combine(d.FullName,"EscapeFromTarkov.exe")))return System.IO.Path.Combine(d.FullName,"Tools","ZombieSurvival","diagnostics");d=d.Parent;}
        return System.IO.Path.Combine(AppContext.BaseDirectory,"user","logs","sptz");
    }
    private void InitializeDiagnostics()
    {
        var httpPath=System.IO.Path.Combine(AppContext.BaseDirectory,"SPT_Data","configs","http.json");
        var port=JsonNode.Parse(File.ReadAllText(httpPath))?["port"]?.ToString()??"unknown";
        var mode=Config["instanceKind"]?.ToString()=="Development"?"dev":"play";
        _journal=new ServerJournal(DiagnosticRoot(),$"{DateTime.Now:HHmmss}-{mode}-{port}-{Environment.ProcessId}");
        _journal.Record("startup",new {version=Config["version"]?.ToString(),port,mode,assemblyHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))),config=Config.DeepClone(),campaignTitle=_campaign["title"]?.ToString()});
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>_journal.Record("unhandled_exception",new {error=Redact(e.ExceptionObject?.ToString()??"")});
        TaskScheduler.UnobservedTaskException+=(_,e)=>_journal.Record("unobserved_task_exception",new {error=Redact(e.Exception.ToString())});
    }
    private string Redact(string value)
    {
        foreach(var id in saves.GetProfiles().Keys)value=value.Replace(id.ToString(),"player:"+PlayerKey(id.ToString()),StringComparison.Ordinal);
        return Regex.Replace(value,@"(?i)(?:-token=|PHPSESSID=|Authorization:\s*(?:Bearer\s+)?)[^\s,;]+","[redacted]");
    }
    private JsonNode ProfileDiagnostic(MongoId session)
    {
        if(!saves.GetProfiles().TryGetValue(session,out var full)||full.CharacterData?.PmcData is not { } p)return new JsonObject();
        var n=DiagnosticNode(p)!;
        return new JsonObject{["level"]=n["Info"]?["Level"]?.DeepClone(),["experience"]=n["Info"]?["Experience"]?.DeepClone(),["health"]=n["Health"]?.DeepClone(),["inventory"]=n["Inventory"]?.DeepClone(),["quests"]=n["Quests"]?.DeepClone(),["counters"]=n["TaskConditionCounters"]?.DeepClone(),["quartermaster"]=n["TradersInfo"]?[TraderId]?.DeepClone(),["unlocked"]=n["UnlockedInfo"]?.DeepClone()};
    }
    private void Trace(string type,MongoId session,object? data)=>_journal?.Record(type,data,PlayerKey(session.ToString()),_raidIds.GetValueOrDefault(session.ToString()),session.ToString());
    private void CaptureDiagnostic(Action capture)
    {
        try{capture();}catch(Exception e){_journal?.Record("capture.error",new {error=Redact(e.ToString())});}
    }
    private void RaidTraceStart(MongoId session,string map)=>CaptureDiagnostic(()=>RaidTraceStartUnsafe(session,map));
    private void RaidTraceStartUnsafe(MongoId session,string map)
    {
        if(_journal is null)return;
        var id=$"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";_raidIds[session.ToString()]=id;
        var spec=MapSpecFor(session,map)!;
        var context=new JsonObject{["raid"]=id,["player"]=PlayerKey(session.ToString()),["serverRun"]=_journal.Run,["version"]=Config["version"]?.DeepClone(),["map"]=map,["expected"]=spec["total"]?.DeepClone(),["populationMode"]=JsonValue.Create("finiteWaves"),["activeCap"]=JsonValue.Create(WaveCap(map,(int)spec["total"]!)),["shooters"]=spec["shooters"]?.DeepClone(),["crisis"]=spec["crisisName"]?.DeepClone(),["utc"]=DateTimeOffset.UtcNow.ToString("O")};
        if(spec["populationMultiplier"] is { } populationMultiplier)context["populationMultiplier"]=populationMultiplier.DeepClone();
        var dir=System.IO.Path.Combine(_journal.Root,"contexts");Directory.CreateDirectory(dir);
        var path=System.IO.Path.Combine(dir,PlayerKey(session.ToString())+".json");var temp=path+".tmp";File.WriteAllText(temp,context.ToJsonString());File.Move(temp,path,true);
        Trace("raid.start",session,new {context,profile=ProfileDiagnostic(session)});
    }
    public void DiagnosticRaidEnd(MongoId session,object? request,bool after)=>CaptureDiagnostic(()=>DiagnosticRaidEndUnsafe(session,request,after));
    private void DiagnosticRaidEndUnsafe(MongoId session,object? request,bool after)
    {
        JsonNode? summary=null;
        if(!after&&request is not null)
        {
            var n=DiagnosticNode(request)!;
            summary=new JsonObject();
            foreach(var key in new[]{"exitStatus","exitName","raidId","result","results","isPlayerScav","location"})if(n[key] is {} v)summary[key]=v.DeepClone();
        }
        Trace(after?"raid.saved":"raid.end_received",session,new {request=summary,profile=ProfileDiagnostic(session)});
        _journal?.Flush();
    }
    public void DiagnosticItemRequest(MongoId session,object info)
    {
        CaptureDiagnostic(()=>{var n=DiagnosticNode(info)!;Trace("item.request",session,new {actions=n["data"]?.DeepClone()});});
    }
    // Native EventOutputHolder resets a shared response dictionary before and after
    // each asynchronous item transaction. A quest-controller lock starts too late.
    private readonly SemaphoreSlim _itemEventGate=new(1,1);
    public sealed class ItemEventOperation(SemaphoreSlim gate)
    {
        private int _released;
        public void Release(){if(Interlocked.Exchange(ref _released,1)==0)gate.Release();}
    }
    public ItemEventOperation EnterItemEvent(){_itemEventGate.Wait();return new ItemEventOperation(_itemEventGate);}
    public async ValueTask<T> DiagnosticItemResponse<T>(ValueTask<T> task,MongoId session,ItemEventOperation? operation=null)
    {
        try
        {
            var result=await task;
            CaptureDiagnostic(()=>{var n=DiagnosticNode(result)!;Trace("item.response",session,new {warnings=n["warnings"]?.DeepClone(),changes=n["profileChanges"]?[session.ToString()]?.DeepClone()});});
            return result;
        }
        catch(Exception e){Trace("item.error",session,new {error=Redact(e.ToString())});throw;}
        finally{operation?.Release();}
    }
}
