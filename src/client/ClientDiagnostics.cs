using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using EFT;
using EFT.InventoryLogic;
using EFT.HealthSystem;
using Comfort.Common;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Codex.ZombieSurvival.Client;

internal static class ClientDiagnostics
{
    private static readonly ConcurrentQueue<(string File,string? Line,Action<StreamWriter>? WriteSnapshot)> Queue=new();
    private static readonly AutoResetEvent Signal=new(false);
    private static readonly Dictionary<string,DateTime> ErrorTimes=new();
    private static string _root="",_run="",_raid="",_rawSession="",_player="";
    private static float _nextSample,_nextInfection;
    private static long _frames;
    private static double _frameSeconds,_maxFrame;
    // The writer owns delta comparison. Samples carry only detached snapshots.
    private static string _writtenRaid="",_lastInventory="",_lastCounters="";
    private static object? _lastPlayerState;
    private static int _dropped;
    private static int _writing;
    private static bool _started;
    private static float _nextHealTrace;
    private static long _updateTicks,_updateMax,_updateCount;
    private static double _lastCaptureMs;
    internal static void MeasureUpdate(long ticks){_updateTicks+=ticks;_updateMax=Math.Max(_updateMax,ticks);_updateCount++;}
    internal static JObject Context=new();
    private static readonly JsonSerializerSettings JsonSettings=new(){ReferenceLoopHandling=ReferenceLoopHandling.Ignore,FloatFormatHandling=FloatFormatHandling.String};
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(EFT.Interactive.PlaceItemTrigger),"TriggerEnter"),postfix:new HarmonyMethod(typeof(ClientDiagnostics),nameof(PlacementEntered)));
        h.Patch(AccessTools.Method(typeof(EFT.Interactive.PlaceItemTrigger),"TriggerExit"),postfix:new HarmonyMethod(typeof(ClientDiagnostics),nameof(PlacementExited)));
        h.Patch(AccessTools.Method(typeof(Player),"ApplyDamageInfo"),prefix:new HarmonyMethod(typeof(ClientDiagnostics),nameof(BeforeDamage)),postfix:new HarmonyMethod(typeof(ClientDiagnostics),nameof(AfterDamage)));
        h.Patch(AccessTools.Method(typeof(ActiveHealthController),"ChangeHealth"),prefix:new HarmonyMethod(typeof(ClientDiagnostics),nameof(HealthChange)));
    }
    private static readonly Dictionary<string,string[]> PlacementZones=new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"]=new[]{"fuel1","fuel2"},
        ["factory4_day"]=new[]{"place_SADOVOD_01_1","place_SADOVOD_01_2"},
        ["factory4_night"]=new[]{"place_SADOVOD_01_1","place_SADOVOD_01_2"},
        ["interchange"]=new[]{"place_merch_21_1","place_merch_21_2","place_merch_020_1","place_merch_020_2"},
        ["rezervbase"]=new[]{"baraholshik_fuel_area_2","baraholshik_fuel_area_3"}
    };
    private static void PlacementEntered(EFT.Interactive.PlaceItemTrigger __instance,Player __0)=>PlacementChanged(__instance,__0,true);
    private static void PlacementExited(EFT.Interactive.PlaceItemTrigger __instance,Player __0)=>PlacementChanged(__instance,__0,false);
    private static void PlacementChanged(EFT.Interactive.PlaceItemTrigger trigger,Player player,bool entered)
    {
        if(!player.IsYourPlayer||!PlacementZones.Values.Any(zones=>zones.Contains(trigger.Id)))return;
        try
        {
            var p=player.Transform.position;
            Record(entered?"quest.placement_zone_enter":"quest.placement_zone_exit",new {zone=trigger.Id,location=player.Location,position=new{p.x,p.y,p.z},quests=player.Profile.QuestsData.Where(q=>q.Status.ToString() is "Started" or "AvailableForFinish").Select(q=>new{qid=q.Id,status=q.Status.ToString(),completedConditions=q.CompletedConditions.Select(x=>x.ToString()).ToArray()}).ToArray()});
        }catch(Exception e){Record("quest.placement_event_error",new{error=e.ToString()});}
    }
    private static void CapturePlacementZones(GameWorld world)
    {
        if(world.MainPlayer is null||!PlacementZones.TryGetValue(world.MainPlayer.Location,out var expected))return;
        var started=System.Diagnostics.Stopwatch.GetTimestamp();
        var found=UnityEngine.Object.FindObjectsOfType<EFT.Interactive.PlaceItemTrigger>(true).Where(t=>expected.Contains(t.Id)).Take(24).ToArray();
        var zones=found.Select(t=>{var c=t.GetComponent<Collider>();var p=c is not null&&c.enabled&&t.gameObject.activeInHierarchy?c.bounds.center:t.transform.position;return new{id=t.Id,active=t.gameObject.activeInHierarchy,enabled=t.enabled,colliderEnabled=c?.enabled,scene=t.gameObject.scene.name,position=new{p.x,p.y,p.z}};}).ToArray();
        Record("quest.placement_zones",new {map=world.MainPlayer.Location,expected,found=zones,missing=expected.Where(id=>!found.Any(t=>t.Id==id)).ToArray(),captureMs=1000.0*(System.Diagnostics.Stopwatch.GetTimestamp()-started)/System.Diagnostics.Stopwatch.Frequency});
    }
    private static void HealthChange(ActiveHealthController __instance,EBodyPart __0,float __1,DamageInfoStruct __2)
    {
        try
        {
            if(__1<=.5f||Time.unscaledTime<_nextHealTrace||!Singleton<GameWorld>.Instantiated||!ReferenceEquals(Singleton<GameWorld>.Instance.MainPlayer?.HealthController,__instance))return;
            _nextHealTrace=Time.unscaledTime+30;
            Record("combat.healing",new {part=__0.ToString(),amount=__1,cause=__2.DamageType.ToString(),stack=new System.Diagnostics.StackTrace(1,false).ToString()});
        }catch { }
    }
    private static void BeforeDamage(Player __instance,EBodyPart __1,out float __state)
    {
        __state=-1;
        try{__state=__instance.HealthController.GetBodyPartHealth(__1).Current;}catch { }
    }
    private static void AfterDamage(Player __instance,DamageInfoStruct __0,EBodyPart __1,float __3,float __state)
    {
        try
        {
            var owner=Get(__0.Player,"iPlayer") as Player;
            Record("combat.damage",new {target=Key(__instance.Profile.Id),isPlayer=__instance.IsYourPlayer,role=__instance.Profile.Info.Settings?.Role.ToString(),part=__1.ToString(),cause=__0.DamageType.ToString(),incoming=__0.Damage,absorbed=__3,before=__state,after=__instance.HealthController.GetBodyPartHealth(__1).Current,weapon=__0.Weapon?.StringTemplateId,source=__0.SourceId,attacker=owner is null?null:Key(owner.Profile.Id),distance=owner is null?(float?)null:Vector3.Distance(owner.Transform.position,__instance.Transform.position)});
        }
        catch { }
    }
    internal static string Key(string raw)
    {
        using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-","").ToLowerInvariant().Substring(0,16);
    }
    private static string Scrub(string text,string rawSession,string player)
    {
        if(rawSession.Length>0)text=text.Replace(rawSession,"player:"+player);
        return Regex.Replace(text,@"(?i)(?:-token=|PHPSESSID=|Authorization:\s*(?:Bearer\s+)?)[^\s,;]+","[redacted]");
    }
    private static string LogFile(string raid,string run)=>Path.Combine(_root,DateTime.Now.ToString("yyyy-MM-dd"),(raid.Length>0?raid:run)+"-client.jsonl");
    internal static void Start()
    {
        if(_started)return;_started=true;
        _root=Path.GetFullPath(Path.Combine(BepInEx.Paths.PluginPath,"..","..","Tools","ZombieSurvival","diagnostics"));Directory.CreateDirectory(_root);
        _run=DateTime.Now.ToString("HHmmss")+"-client-"+Guid.NewGuid().ToString("N").Substring(0,8);
        RefreshIdentity();
        Task.Factory.StartNew(WriteLoop,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        Task.Run(()=>Cleanup());
        Application.logMessageReceivedThreaded+=UnityLog;
        Application.quitting+=Flush;
        var mods=Chainloader.PluginInfos.Values.Select(p=>new {id=p.Metadata.GUID,version=p.Metadata.Version.ToString()}).ToArray();
        Record("startup",new {version=typeof(Plugin).Assembly.GetName().Version?.ToString(3),gameVersion=Application.version,unity=Application.unityVersion,mods,screen=new{Screen.width,Screen.height,Screen.fullScreen},hearing=new{Plugin.LoudMin,Plugin.LoudMax,Plugin.QuietMax,Plugin.FootstepRange,settings=Plugin.Hearing,patrol=Plugin.Patrol}});
    }
    private static void RefreshIdentity()
    {
        try
        {
            var type=AccessTools.TypeByName("SPT.Common.Http.RequestHandler");
            _rawSession=type is null?"":AccessTools.Field(type,"SessionId")?.GetValue(null)?.ToString()??"";
            _player=Key(_rawSession);
        }
        catch { }
    }
    internal static void Record(string type,object? data)
    {
        if(!_started)return;
        try
        {
            if(Queue.Count>2048){Interlocked.Increment(ref _dropped);return;}
            var line=Scrub(JsonConvert.SerializeObject(new {utc=DateTimeOffset.UtcNow,run=_run,source="client",type,player=_player,raid=_raid,data,dropped=Interlocked.Exchange(ref _dropped,0)},JsonSettings),_rawSession,_player);
            Queue.Enqueue((LogFile(_raid,_run),line,null));Signal.Set();
        }
        catch {Interlocked.Increment(ref _dropped);}
    }
    private static void RecordRaidSample(object sample,object[]? inventory,object[]? counters)
    {
        if(!_started)return;
        if(Queue.Count>2048){Interlocked.Increment(ref _dropped);return;}
        var utc=DateTimeOffset.UtcNow;var raid=_raid;var run=_run;var player=_player;var session=_rawSession;
        var dropped=Interlocked.Exchange(ref _dropped,0);
        Queue.Enqueue((LogFile(raid,run),null,writer=>
        {
            void Write(string type,object data,int skipped=0) => writer.WriteLine(Scrub(JsonConvert.SerializeObject(
                new {utc,run,source="client",type,player,raid,data,dropped=skipped},JsonSettings),session,player));
            if(_writtenRaid!=raid){_writtenRaid=raid;_lastInventory="";_lastCounters="";}
            Write("raid.sample",sample,dropped);
            if(inventory is not null)
            {
                var current=JsonConvert.SerializeObject(inventory,JsonSettings);
                if(current!=_lastInventory){Write("inventory.change",inventory);_lastInventory=current;}
            }
            if(counters is not null)
            {
                var current=JsonConvert.SerializeObject(counters,JsonSettings);
                if(current!=_lastCounters){Write("quest.counters",counters);_lastCounters=current;}
            }
        }));
        Signal.Set();
    }
    private static void WriteLoop()
    {
        while(true)
        {
            Signal.WaitOne(1500);
            Volatile.Write(ref _writing,1);
            try
            {
                var rows=new List<(string File,string? Line,Action<StreamWriter>? WriteSnapshot)>();while(Queue.TryDequeue(out var row))rows.Add(row);
                foreach(var group in rows.GroupBy(r=>r.File))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(group.Key)!);
                    var file=group.Key;var part=0;while(File.Exists(file)&&new FileInfo(file).Length>33554432)file=group.Key.Replace(".jsonl","-part"+(++part)+".jsonl");
                    using var stream=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.ReadWrite);
                    using var writer=new StreamWriter(stream,new UTF8Encoding(false));
                    foreach(var row in group)
                        if(row.WriteSnapshot is { } snapshot)
                        {
                            try{snapshot(writer);}catch{Interlocked.Increment(ref _dropped);}
                        }
                        else writer.WriteLine(row.Line);
                }
            }
            catch {while(Queue.TryDequeue(out _)){} }
            finally {Volatile.Write(ref _writing,0);}
        }
    }
    private static void Cleanup()
    {
        try
        {
            var prefix=Path.GetFullPath(_root)+Path.DirectorySeparatorChar;
            var dirs=Directory.GetDirectories(_root).Where(d=>Regex.IsMatch(Path.GetFileName(d),@"^\d{4}-\d{2}-\d{2}$")&&(File.GetAttributes(d)&FileAttributes.ReparsePoint)==0);
            var files=dirs.SelectMany(d=>Directory.GetFiles(d,"*.jsonl")).Select(p=>new FileInfo(p)).OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
            var keep=new HashSet<string>(files.Take(100).Select(f=>f.FullName),StringComparer.OrdinalIgnoreCase);var bytes=files.Sum(f=>f.Length);
            foreach(var f in files.Reverse())if(!keep.Contains(f.FullName)&&f.FullName.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&(f.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-30)||bytes>1610612736L)){var length=f.Length;f.Delete();bytes-=length;}
        }
        catch { }
    }
    private static void UnityLog(string message,string stack,LogType type)
    {
        if(type is not (LogType.Error or LogType.Exception or LogType.Assert or LogType.Warning))return;
        var key=type+":"+message;
        lock(ErrorTimes)
        {
            if(ErrorTimes.TryGetValue(key,out var last)&&DateTime.UtcNow-last<TimeSpan.FromSeconds(60))return;
            if(ErrorTimes.Count>512)ErrorTimes.Clear();ErrorTimes[key]=DateTime.UtcNow;
        }
        Record("unity."+type.ToString().ToLowerInvariant(),new {message,stack});
    }
    internal static void BeginRaid(GameWorld world)
    {
        RefreshIdentity();_raid="client-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
        try
        {
            var path=Path.Combine(_root,"contexts",_player+".json");
            if(File.Exists(path))
            {
                var context=JObject.Parse(File.ReadAllText(path));
                if(DateTimeOffset.TryParse((string?)context["utc"],out var time)&&DateTimeOffset.UtcNow-time<TimeSpan.FromMinutes(20))
                {Context=context;_raid=(string?)context["raid"]??_raid;}
            }
        }
        catch(Exception e){Record("context.error",new {error=e.ToString()});}
        _frames=0;_frameSeconds=0;_maxFrame=0;_nextSample=Time.unscaledTime+1;_updateTicks=0;_updateMax=0;_updateCount=0;_lastCaptureMs=0;
        _lastPlayerState=PlayerState(world.MainPlayer);
        Record("raid.start",new {context=Context,player=_lastPlayerState,screen=new{Screen.width,Screen.height,Screen.fullScreen},render=new {targetFrameRate=Application.targetFrameRate,vSyncCount=QualitySettings.vSyncCount,quality=QualitySettings.GetQualityLevel(),gpu=SystemInfo.graphicsDeviceName,graphicsMemoryMiB=SystemInfo.graphicsMemorySize,cpu=SystemInfo.processorType,cores=SystemInfo.processorCount,memoryMiB=SystemInfo.systemMemorySize},mods=Chainloader.PluginInfos.Values.Select(p=>new {id=p.Metadata.GUID,version=p.Metadata.Version.ToString()}).ToArray()});
        try{CapturePlacementZones(world);}catch(Exception e){Record("quest.placement_capture_error",new{error=e.ToString()});}
    }
    private static object? Get(object? value,string name)=>value is null?null:value.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(value)??value.GetType().GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(value);
    private static object? Value(object? value)=>Get(value,"Current")??Get(value,"Value")??value?.ToString();
    private static string? ParentLabel(Item item){try{return item.Parent?.ToString();}catch{return null;}}
    internal static object[] Inventory(InventoryEquipment equipment)=>equipment.GetAllItems().OrderBy(i=>i.Id).Select(i=>(object)new {id=i.Id,tpl=i.StringTemplateId,count=i.StackObjectsCount,fir=i.SpawnedInSession,parent=ParentLabel(i),durability=i.GetItemComponent<RepairableComponent>()?.Durability,maxDurability=i.GetItemComponent<RepairableComponent>()?.MaxDurability}).ToArray();
    private static object[] Counters(Player p)=>p.Profile.TaskConditionCounters.OrderBy(x=>x.Key.ToString()).Select(x=>(object)new {id=x.Key.ToString(),type=x.Value.Type,value=x.Value.Value,sourceId=x.Value.SourceId}).ToArray();
    private static object? PlayerState(Player? p,object[]? inventory=null,object[]? counters=null)
    {
        if(p is null)return null;
        var health=new Dictionary<string,object?>();
        foreach(var part in new[]{EBodyPart.Head,EBodyPart.Chest,EBodyPart.Stomach,EBodyPart.LeftArm,EBodyPart.RightArm,EBodyPart.LeftLeg,EBodyPart.RightLeg})
        {var h=p.HealthController.GetBodyPartHealth(part);health[part.ToString()]=new {h.Current,h.Maximum};}
        var effects=new List<object>();
        try
        {
            foreach(var effect in p.HealthController.GetAllActiveEffects(EBodyPart.Common))
                effects.Add(new {type=effect.GetType().Name,part=Get(effect,"BodyPart")?.ToString(),source=Get(effect,"ItemTemplateId"),name=Get(effect,"Name"),buffs=(Get(effect,"ActiveBuffs") as IEnumerable)?.Cast<object>().Select(b=>new {name=Get(b,"BuffName"),value=Get(b,"Value"),active=Get(b,"Active")}).ToArray()});
        }
        catch { }
        var position=p.Transform.position;
        return new {alive=p.HealthController.IsAlive,location=p.Location,position=new {position.x,position.y,position.z},health,energy=Value(Get(p.HealthController,"Energy")),hydration=Value(Get(p.HealthController,"Hydration")),effects,inventory=inventory??Inventory(p.Inventory.Equipment),counters=counters??Counters(p),quests=p.Profile.QuestsData.Select(q=>new {qid=q.Id,status=q.Status.ToString(),startTime=q.StartTime,completedConditions=q.CompletedConditions.Select(c=>c.ToString()).ToArray()}).ToArray()};
    }
    internal static void Tick(GameWorld world,Func<object> combat)
    {
        _frames++;_frameSeconds+=Time.unscaledDeltaTime;_maxFrame=Math.Max(_maxFrame,Time.unscaledDeltaTime);
        if(Time.unscaledTime<_nextSample)return;_nextSample=Time.unscaledTime+10;
        var captureStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var p=world.MainPlayer;
            var inventory=p is null?null:Inventory(p.Inventory.Equipment);
            var counters=p is null?null:Counters(p);
            var state=PlayerState(p,inventory,counters);_lastPlayerState=state;
            RecordRaidSample(new {fps=_frameSeconds>0?_frames/_frameSeconds:0,maxFrameMs=_maxFrame*1000,managedBytes=GC.GetTotalMemory(false),focused=Application.isFocused,renderState=new {Application.targetFrameRate,vSyncCount=QualitySettings.vSyncCount,fullScreenMode=Screen.fullScreenMode.ToString(),Screen.width,Screen.height,Time.timeScale,Time.fixedDeltaTime,Time.maximumDeltaTime,Time.captureFramerate},customUpdateMeanMs=_updateCount>0?1000.0*_updateTicks/System.Diagnostics.Stopwatch.Frequency/_updateCount:0,customUpdateMaxMs=1000.0*_updateMax/System.Diagnostics.Stopwatch.Frequency,previousCaptureMs=_lastCaptureMs,combat=combat(),player=state},inventory,counters);
            _frames=0;_frameSeconds=0;_maxFrame=0;
            _updateTicks=0;_updateMax=0;_updateCount=0;
        }
        catch(Exception e){Record("sample.error",new {error=e.ToString()});}
        finally{_lastCaptureMs=1000.0*(System.Diagnostics.Stopwatch.GetTimestamp()-captureStarted)/System.Diagnostics.Stopwatch.Frequency;}
    }
    internal static void Death(Player p,EDamageType cause)
    {
        try{Record(p.IsYourPlayer?"player.death":"bot.death",new {id=Key(p.Profile.Id),role=p.Profile.Info.Settings?.Role.ToString(),cause=cause.ToString(),position=new {p.Transform.position.x,p.Transform.position.y,p.Transform.position.z},player=p.IsYourPlayer?PlayerState(p):null});}catch(Exception e){Record("death.capture_error",new {error=e.ToString()});}
    }
    internal static void Infection(float intensity)
    {
        if(intensity<=.001f||Time.unscaledTime<_nextInfection)return;_nextInfection=Time.unscaledTime+5;Record("infection.visual_bypassed",new {intensity});
    }
    internal static void EndRaid(GameWorld world)
    {
        try{Record("raid.world_destroy",new {player=PlayerState(world.MainPlayer),lastSampleFallback=false});}catch(Exception e){Record("raid.world_destroy",new {player=_lastPlayerState,lastSampleFallback=true,captureError=e.Message});}
        Flush();
    }
    private static void Flush(){Signal.Set();for(var i=0;i<20&&(!Queue.IsEmpty||Volatile.Read(ref _writing)!=0);i++)Thread.Sleep(10);}
}
