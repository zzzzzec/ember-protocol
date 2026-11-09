using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Codex.ZombieSurvival.Rules;
namespace Codex.ZombieSurvival.Client;

internal static class FiniteWaves
{
    private sealed class Entry
    {
        public BossLocationSpawn Wave=null!;
        public int Ticket;
        public int Batch;
        public bool Issued,InFlight,HandedOff;
        public bool FinalSafetyPassed;
        public string ProfileId="";
        public BotCreationDataClass? Deferred;
        public BotSpawnParams? Parameters;
        public float RetryAt;
        public float LastDeferral=-100;
    }
    private sealed class Raid
    {
        public BotSpawner Spawner=null!;
        public WaveBudget Budget=null!;
        public readonly Dictionary<BossLocationSpawn,Entry> Entries=new();
        public readonly Dictionary<int,Entry> Tickets=new();
        public readonly Dictionary<string,Player> Actors=new();
        public int OpeningCap;
        public bool PreferNearby;
        public Func<int,int,double>? ReinforcementPriority;
        public Func<int,bool>? EligibleReinforcement;
        private readonly Dictionary<string,float> _zoneRetryAt=new(StringComparer.Ordinal);

        public bool CanAdmit(int ticket)
        {
            var entry=Tickets[ticket];
            return !entry.InFlight&&Time.time>=entry.RetryAt
                && (!_zoneRetryAt.TryGetValue(entry.Wave.BossZone,out var retry)||Time.time>=retry);
        }

        public void HoldUnsafeZone(Entry entry)
        {
            if (!Budget.ReturnUnbound(entry.Ticket)) return;
            entry.Issued=false;
            entry.RetryAt=Time.time+18;
            _zoneRetryAt[entry.Wave.BossZone]=entry.RetryAt;
            ClientDiagnostics.Record("waves.slot_released",new{ticket=entry.Ticket,zone=entry.Wave.BossZone,retryAfter=18,budget=Snapshot()});
        }
        public readonly Dictionary<string,float> ZoneScores=new(StringComparer.Ordinal);
        public float ZoneScoreTime=-1;
        public bool Closed,ReportedClear;
        public float LastSample,LastPendingReport,NextReconcile;
        public void Created(BotOwner owner)=>Observe(owner.GetPlayer,"native_event");
        public void Observe(Player player,string source)
        {
            if(Closed||player is null)return;
            var id=player.Profile.Id.ToString();
            var ticket=Budget.TicketForProfile(id);
            if(ticket.HasValue&&Tickets[ticket.Value].Batch>1&&!Tickets[ticket.Value].FinalSafetyPassed)return;
            if(!Budget.IsBound(id)||!Budget.Created(id,Time.time))return;
            Actors[id]=player;
            SurvivalRaid.Current?.Added(player);
            if(player.HealthController is not null&&!player.HealthController.IsAlive)Budget.Died(id);
            ClientDiagnostics.Record("waves.actor_spawned",new{source,actor=ClientDiagnostics.Key(id),budget=Snapshot()});
        }
        public object Snapshot()=>new{planned=Budget.Total,openingCap=OpeningCap,preferNearby=PreferNearby,issued=Budget.Issued,spawned=Budget.Spawned,alive=Budget.Alive,pending=Budget.Pending,waiting=Budget.Waiting,cap=Budget.Cap,mode=Budget.Steady?"steady":"batch",pendingLimit=Budget.PendingLimit,activating=Budget.Activating,nextActivationAt=Budget.NextActivationAt,spacing=Budget.Spacing,jitter=Budget.Jitter,seed=Budget.Seed,deferred=Tickets.Values.Count(e=>e.Deferred is not null),native=new{alive=Spawner.AllBotsCount,loading=Spawner.InSpawnProcess,delayed=Spawner.SpawnDelaysService.WaitCount},batch=Budget.Batch,threshold=Budget.Threshold,sealedPlan=Budget.Sealed,cleared=Budget.Cleared};
    }
    private static JObject Settings=new();
    private static bool Enabled;
    private static Raid? Current;
    internal static void Install(Harmony harmony)
    {
        var path=Path.Combine(BepInEx.Paths.PluginPath,"Codex-ZombieSurvival","wave-settings.json");
        try{Settings=JObject.Parse(File.ReadAllText(path));Enabled=(bool?)Settings["enabled"]==true;}
        catch(Exception e){Plugin.Log.LogError("[SPT-Z] Wave settings unavailable; finite waves disabled: "+e.Message);Enabled=false;}
        harmony.Patch(AccessTools.Method(typeof(BotSpawner),"ActivateBotsByWave",new[]{typeof(BossLocationSpawn)}),prefix:new HarmonyMethod(typeof(FiniteWaves),nameof(Capture)));
        harmony.Patch(AccessTools.Method(typeof(BossSpawnScenario),"Run"),postfix:new HarmonyMethod(typeof(FiniteWaves),nameof(Seal)));
        harmony.Patch(AccessTools.Method(typeof(BossSpawnerClass),"method_3"),prefix:new HarmonyMethod(typeof(FiniteWaves),nameof(BeforePosition)));
        harmony.Patch(AccessTools.Method(typeof(BossSpawnerClass),"method_0"),prefix:new HarmonyMethod(typeof(FiniteWaves),nameof(Delay)));
        harmony.Patch(AccessTools.Method(typeof(BotSpawner),"Stop"),prefix:new HarmonyMethod(typeof(FiniteWaves),nameof(Stop)));
        harmony.Patch(typeof(BotCreatorClass).GetConstructors().Single(c=>c.GetParameters().Length==3),postfix:new HarmonyMethod(typeof(FiniteWaves),nameof(WrapFactory)));
    }
    private static string Map()=>Singleton<GameWorld>.Instantiated?(Singleton<GameWorld>.Instance.MainPlayer?.Location??Singleton<GameWorld>.Instance.LocationId??"").ToLowerInvariant():"";
    internal static bool IsEnabled=>Enabled;
    private static Raid Get(BotSpawner spawner)
    {
        if(Current?.Spawner==spawner&&!Current.Closed)return Current;
        if(Current is not null)Close(Current);
        var map=Map();var configured=(int?)Settings["aliveCaps"]?[map]??WaveLimits.DefaultCap(map);
        // The location's BotMax is the server's copy of the same authored cap.
        var cap=spawner.MaxBots>0?Math.Min(configured,spawner.MaxBots):configured;
        var steady=(string?)Settings["mode"]=="steady";
        var seed=unchecked((uint)DateTime.UtcNow.Ticks);
        var raid=new Raid{Spawner=spawner,Budget=new WaveBudget(cap,(int?)Settings["replenishAt"]??2,(double?)Settings["breatherSeconds"]??8,(double?)Settings["spawnSpacingSeconds"]??.35,steady,(int?)Settings["pendingLimit"]??2,(double?)Settings["spacingJitter"]??.25,seed)};
        raid.OpeningCap=Math.Max(1,Math.Min(cap,(int?)Settings["openingCaps"]?[map]??cap));
        raid.PreferNearby=Settings["nearbyReinforcements"] is JArray maps&&maps.Values<string>().Contains(map);
        if(raid.PreferNearby)
        {
            raid.ReinforcementPriority=(first,candidate)=>ReinforcementScore(raid,first,candidate);
            raid.EligibleReinforcement=raid.CanAdmit;
        }
        Current=raid;spawner.OnBotCreated+=raid.Created;return raid;
    }
    private static bool Capture(BotSpawner __instance,BossLocationSpawn __0)
    {
        if(!Enabled||__0 is null||!__0.BossType.ToString().StartsWith("infected",StringComparison.OrdinalIgnoreCase))return true;
        var raid=Get(__instance);
        if(raid.Closed||__instance.GameEnd)return false;
        if(raid.Entries.TryGetValue(__0,out var existing))return false; // native repeated dispatch is not a new budget entry
        if(__0.EscortCount!=0){Plugin.Log.LogError("[SPT-Z] Finite wave contract requires one infected per entry.");return false;}
        var entry=new Entry{Wave=__0,Ticket=raid.Entries.Count};
        if(!raid.Budget.Register(entry.Ticket))return false;
        raid.Entries.Add(__0,entry);raid.Tickets.Add(entry.Ticket,entry);
        if(raid.Budget.Issued>=raid.OpeningCap||!raid.Budget.Opening(entry.Ticket))return false;
        entry.Issued=true;entry.Batch=1;return true;
    }
    private static void Seal(BossSpawnScenario __instance)
    {
        var raid=Current;if(raid is null||raid.Closed||raid.Budget.Sealed)return;
        if(!__instance.BossSpawnWaves.Any(w=>raid.Entries.ContainsKey(w)))return;
        raid.Budget.Seal();ClientDiagnostics.Record("waves.plan",raid.Snapshot());
        Plugin.Log.LogInfo(raid.Budget.Steady
            ?$"[SPT-Z] Finite steady replenishment: total {raid.Budget.Total}, simultaneous cap {raid.Budget.Cap}, one activation per paced interval."
            :$"[SPT-Z] Finite waves: total {raid.Budget.Total}, simultaneous cap {raid.Budget.Cap}, next batch at <= {raid.Budget.Threshold} survivors.");
    }
    internal static object Diagnostic()=>Current?.Snapshot()??new{enabled=Enabled,initialized=false};
    internal static void Tick()
    {
        var raid=Current;if(raid is null||raid.Closed||raid.Spawner.GameEnd)return;
        if(Time.time>=raid.NextReconcile&&Singleton<GameWorld>.Instantiated)
        {
            raid.NextReconcile=Time.time+1;
            // Startup activation can precede native multicast subscriptions. Only profiles
            // already bound to an issued ticket can reconcile a missed creation event.
            foreach(var player in Singleton<GameWorld>.Instance.AllAlivePlayersList.OfType<Player>())raid.Observe(player,"world_reconcile");
        }
        foreach(var pair in raid.Actors)
            if(pair.Value==null||pair.Value.HealthController?.IsAlive!=true)raid.Budget.Died(pair.Key);
        var previous=raid.Budget.Batch;
        // Establish the authored hotspot population before replacing losses near the player.
        var priority=raid.Budget.Issued>=raid.Budget.Cap?raid.ReinforcementPriority:null;
        var ticket=raid.Budget.TakeNext(Time.time,priority,raid.EligibleReinforcement);
        if(!raid.Budget.Steady&&raid.Budget.Batch!=previous)ClientDiagnostics.Record("waves.batch",raid.Snapshot());
        if(ticket.HasValue)
        {
            var e=raid.Tickets[ticket.Value];e.Issued=true;e.Batch=raid.Budget.Batch;
            if(raid.Budget.Steady)ClientDiagnostics.Record("waves.reinforcement_requested",new{ticket=ticket.Value,zone=e.Wave.BossZone,openingFill=priority is null&&raid.PreferNearby&&raid.Budget.Issued<=raid.Budget.Cap,budget=raid.Snapshot()});
            // Initial entries have already passed through the native startup path.
            // Deferred entries use the normal, safety-filtered native spawn path.
            // Our budget includes all active AND loading actors. Native InSpawnProcess
            // can retain stale entries, so it is not a second source of population truth.
            // Native fallback may reuse points whose cooldown/individual-use limit
            // rejected them. Our own hard visibility, distance and activation gates
            // still apply; this never forces a model to appear near or in view.
            e.Wave.Time=1;e.Wave.ForceSpawn=true;e.Wave.IgnoreMaxBots=true;
            var parameters=e.Parameters??new BotSpawnParams{TriggerType=e.Wave.TriggerType,Id_spawn=e.Wave.TriggerId};
            var deferred=e.Deferred;e.Deferred=null;
            Request(raid,e,parameters,deferred);
        }
        // One retry per low-frequency tick, using the same profile and reservation.
        foreach(var entry in raid.Tickets.Values)
        {
            if(!entry.Issued||entry.Deferred is null||entry.InFlight||entry.HandedOff||Time.time<entry.RetryAt)continue;
            var data=entry.Deferred;entry.Deferred=null;
            Request(raid,entry,entry.Parameters!,data);break;
        }
        if(Time.time>=raid.LastSample+10){raid.LastSample=Time.time;ClientDiagnostics.Record("waves.sample",raid.Snapshot());}
        if(raid.Budget.Pending>0&&Time.time>=raid.LastPendingReport+60){raid.LastPendingReport=Time.time;ClientDiagnostics.Record("waves.pending",raid.Snapshot());}
        if(raid.Budget.Cleared&&!raid.ReportedClear){raid.ReportedClear=true;ClientDiagnostics.Record("waves.cleared",raid.Snapshot());}
    }
    private static async void Request(Raid raid,Entry entry,BotSpawnParams parameters,BotCreationDataClass? data=null)
    {
        if(raid.Closed||entry.InFlight||entry.HandedOff)return;
        entry.InFlight=true;
        try{await raid.Spawner.BossSpawner.Spawn(entry.Wave,parameters,data);}
        catch(Exception e){Plugin.Log.LogError("[SPT-Z] Native wave request failed: "+e);ClientDiagnostics.Record("waves.spawn_error",new{ticket=entry.Ticket,error=e.ToString()});}
        finally{entry.InFlight=false;}
    }
    private static void Defer(Entry entry,BotSpawnParams parameters,BotCreationDataClass data,string reason="position")
    {
        if(entry.HandedOff)return;
        entry.Deferred=data;entry.Parameters=parameters;entry.RetryAt=Time.time+3;
        if(Time.time>entry.LastDeferral+10){entry.LastDeferral=Time.time;ClientDiagnostics.Record(reason=="pacing"?"waves.activation_deferred":"waves.position_deferred",new{ticket=entry.Ticket,zone=entry.Wave.BossZone,batch=entry.Batch,reason});}
    }
    private static bool Delay(BossSpawnerClass __instance,BossLocationSpawn __0,BotSpawnParams __1,BotCreationDataClass __2)
    {
        var raid=Current;
        if(raid is null||raid.Closed||raid.Spawner!=__instance.BotSpawner_0||!raid.Entries.TryGetValue(__0,out var entry)||!entry.Issued)return true;
        // Native delay queues have independent capacities/timeouts. Hold this exact
        // request locally instead; never regenerate a profile to replace a dropped queue entry.
        Defer(entry,__1,__2,"native_delay");return false;
    }
    private static bool BeforePosition(BossSpawnerClass __instance,BotCreationDataClass __0,BossLocationSpawn __1,BotSpawnParams __2,BotZone __4,List<ISpawnPoint> __5,ref Task __result)
    {
        if(__instance.BotSpawner_0.GameEnd){__result=Task.CompletedTask;return false;}
        var raid=Current;
        if(raid is null||raid.Closed||raid.Spawner!=__instance.BotSpawner_0||!raid.Entries.TryGetValue(__1,out var entry)||!entry.Issued)return true;
        if(entry.HandedOff){__result=Task.CompletedTask;return false;}
        if(entry.Batch>1&&raid.PreferNearby)
        {
            var point=NearbySafePoint(__instance,__0,__1,__4,__5);
            __5.Clear();
            if(point is not null)__5.Add(point);
        }
        else if(entry.Batch>1&&(__5.Count==0||!Safe(__5[0].Position)))
        {
            // Ask the existing selector for a few alternatives in the same authored zone.
            // This avoids repeatedly selecting one blocked point; it does not move existing actors.
            var alternative=__5.FirstOrDefault(p=>Safe(p.Position));
            if(alternative is null)
            {
                var candidates=__instance.ISpawnSystem.SelectAISpawnPoints(__0,__4,8,__1.PerfectPos,ActionIfNotEnoughPoints.FillWithDiscardedPointsAndDuplicates,__instance.List_0);
                alternative=candidates.FirstOrDefault(p=>Safe(p.Position));
            }
            if(alternative is not null){__5.Clear();__5.Add(alternative);}
        }
        if(entry.Batch>1&&__5.Count==0)
        {
            Defer(entry,__2,__0);
            // Keep the prepared native profile, but free the slot before testing
            // another authored area. Never release a bound or visible actor.
            if(raid.PreferNearby)raid.HoldUnsafeZone(entry);
            __result=Task.CompletedTask;return false;
        }
        if(__0.Profiles.Count!=1||!raid.Budget.Bind(entry.Ticket,__0.Profiles[0].Id.ToString()))
        {
            Plugin.Log.LogError("[SPT-Z] Duplicate or invalid wave profile; spawn blocked.");ClientDiagnostics.Record("waves.profile_rejected",new{ticket=entry.Ticket,count=__0.Profiles.Count,unique=__0.Profiles.Where(p=>p is not null).Select(p=>p.Id.ToString()).Distinct().Count()});__result=Task.CompletedTask;return false;
        }
        entry.ProfileId=__0.Profiles[0].Id.ToString();entry.HandedOff=true;entry.Deferred=null;
        return true;
    }
    private static float MinimumSpawnDistance(string map)
        => map.StartsWith("factory4") ? (float?)Settings["factoryMinDistance"] ?? 25 : (float?)Settings["minPlayerDistance"] ?? 45;

    private static Player? LocalHuman()
    {
        if (!Singleton<GameWorld>.Instantiated) return null;
        var player = Singleton<GameWorld>.Instance.MainPlayer;
        return player != null && !player.IsAI && player.HealthController?.IsAlive == true ? player : null;
    }

    private static Vector3 RouteFocus(Player player)
        => player.Transform.position + player.Transform.forward * 25f;

    // Score each native zone once per admission attempt. The base wave order
    // still selects the role, so changing proximity cannot skip the armed quota.
    private static double ReinforcementScore(Raid raid,int first,int candidate)
    {
        var wave = raid.Tickets[candidate].Wave;
        var next = raid.Tickets[first].Wave;
        if (wave.BossType != next.BossType || wave.BossDif != next.BossDif)
            return double.PositiveInfinity;
        if (raid.ZoneScoreTime != Time.time)
        {
            raid.ZoneScores.Clear();
            raid.ZoneScoreTime = Time.time;
        }
        if (raid.ZoneScores.TryGetValue(wave.BossZone,out var cached)) return cached;
        var player = LocalHuman();
        var zone = raid.Spawner.GetZoneByName(wave.BossZone);
        var score = float.PositiveInfinity;
        if (player != null && zone != null && zone.SpawnPoints is { } points)
        {
            var origin = player.Transform.position;
            var focus = RouteFocus(player);
            var minimum = MinimumSpawnDistance(Map());
            foreach (var point in points)
                if (point is not null && !point.SpawnBlocked && (point.Position-origin).sqrMagnitude >= minimum*minimum)
                    score = Mathf.Min(score,(point.Position-focus).sqrMagnitude);
        }
        raid.ZoneScores[wave.BossZone] = score;
        return score;
    }

    private static ISpawnPoint? NearbySafePoint(BossSpawnerClass spawner,BotCreationDataClass data,
        BossLocationSpawn wave,BotZone zone,List<ISpawnPoint> selected)
    {
        var player = LocalHuman();
        var original = selected.Count > 0 && Safe(selected[0].Position) ? selected[0] : null;
        if (player is null || zone == null) return original;
        var origin = player.Transform.position;
        var focus = RouteFocus(player);
        if (original is not null && (original.Position-origin).sqrMagnitude <= 150f*150f) return original;

        // Query a bounded native shortlist only when the default is distant or unsafe.
        // A preferred point still has to pass physical cover and distance checks.
        var minimum = MinimumSpawnDistance(Map());
        var anchor = zone.SpawnPoints?.Where(p=>p is not null&&!p.SpawnBlocked&&(p.Position-origin).sqrMagnitude>=minimum*minimum)
            .OrderBy(p=>(p.Position-focus).sqrMagnitude).FirstOrDefault();
        var candidates=spawner.ISpawnSystem.SelectAISpawnPoints(data,zone,8,anchor?.Position??wave.PerfectPos,
            ActionIfNotEnoughPoints.FillWithDiscardedPointsAndDuplicates,spawner.List_0);
        var originalScore=original is null?float.PositiveInfinity:(original.Position-focus).sqrMagnitude;
        return candidates.Where(p=>p is not null&&(p.Position-focus).sqrMagnitude<originalScore).Distinct()
            .OrderBy(p=>(p.Position-focus).sqrMagnitude).FirstOrDefault(p=>Safe(p.Position)) ?? original;
    }

    private static bool Safe(Vector3 point)
        =>Safe(point,out _,out _);
    private static bool Safe(Vector3 point,out float nearest,out string reason,string? ignoreProfile=null)
    {
        nearest=float.PositiveInfinity;reason="world_unavailable";
        if(!Singleton<GameWorld>.Instantiated)return false;
        var world=Singleton<GameWorld>.Instance;var humans=world.AllAlivePlayersList.OfType<Player>().Where(p=>!p.IsAI&&!Plugin.Infected(p)&&p.HealthController?.IsAlive==true).ToArray();
        if(humans.Length==0){reason="no_player";return false;}
        var minimum=MinimumSpawnDistance(Map());
        foreach(var human in humans)nearest=Math.Min(nearest,Vector3.Distance(point,human.Transform.position));
        if(nearest<minimum){reason="too_close";return false;}
        foreach(var actor in world.AllAlivePlayersList.OfType<Player>())
            if((actor.IsAI||Plugin.Infected(actor))&&actor.HealthController?.IsAlive==true&&actor.Profile.Id.ToString()!=ignoreProfile
                &&(actor.Transform.position-point).sqrMagnitude<6.25f){reason="occupied";return false;}
        foreach(var human in humans)
        {
            var origin=human.Transform.position;
            var side=human.Transform.right*.35f;
            var eyes=new[]{origin+Vector3.up*1.6f,origin+Vector3.up*.65f,origin+Vector3.up*1.6f+side,origin+Vector3.up*1.6f-side};
            var body=new[]{point+Vector3.up*.15f,point+Vector3.up,point+Vector3.up*1.95f,point+Vector3.up*1.4f+side,point+Vector3.up*1.4f-side};
            // Conservative physical cover in every direction, including head/feet,
            // crouching and lean margins. Looking away alone never makes a point safe.
            foreach(var eye in eyes)foreach(var target in body)
                if(!Physics.Linecast(eye,target,LayerMaskClass.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)){reason="visible";return false;}
        }
        reason="safe";return true;
    }
    private static void WrapFactory(BotCreatorClass __instance)
    {
        var original=__instance.Func_0;
        __instance.Func_0=(world,profile,point)=>SafeCreate(original,world,profile,point);
    }
    private static async Task<LocalPlayer> SafeCreate(Func<GameWorld,Profile,Vector3,Task<LocalPlayer>> original,GameWorld world,Profile profile,Vector3 point)
    {
        var raid=Current;var ticket=raid?.Budget.TicketForProfile(profile.Id.ToString());
        if(raid is null||!ticket.HasValue||!raid.Tickets.TryGetValue(ticket.Value,out var entry))
            return await original(world,profile,point);
        if(entry.Batch<=1)
        {
            if(!raid.Budget.TryActivate(entry.Ticket,Time.time))throw new InvalidOperationException("Opening actor was activated twice.");
            return await original(world,profile,point);
        }
        // Keep the native creation task alive while waiting. Completing it early and
        // requeueing the same consumed native data can permanently stop that ticket.
        while(true)
        {
            await WaitForSafety(raid,entry,point,"before_create");
            if(raid.Budget.TryActivate(entry.Ticket,Time.time))break;
            await Task.Delay(500);
        }
        var player=await original(world,profile,point);
        if(player==null)throw new InvalidOperationException("Native reinforcement factory returned no player.");
        var renderers=player.GetComponentsInChildren<Renderer>(true);
        var previous=renderers.Select(r=>r.forceRenderingOff).ToArray();
        foreach(var renderer in renderers)renderer.forceRenderingOff=true;
        try
        {
            // Asset creation is asynchronous: the player may have moved or opened a
            // door since position selection. Keep the new model hidden until rechecked.
            var nearest=await WaitForSafety(raid,entry,point,"before_visible");
            entry.FinalSafetyPassed=true;
            ClientDiagnostics.Record("waves.safe_activation",new{ticket=entry.Ticket,zone=entry.Wave.BossZone,nearestPlayerDistance=nearest,covered=true,solidCoverOnly=true,stage="before_visible",budget=raid.Snapshot()});
        }
        finally
        {
            if(!raid.Closed&&!raid.Spawner.GameEnd)
                for(var i=0;i<renderers.Length;i++)if(renderers[i]!=null)renderers[i].forceRenderingOff=previous[i];
        }
        return player;
    }
    private static async Task<float> WaitForSafety(Raid raid,Entry entry,Vector3 point,string stage)
    {
        while(true)
        {
            if(raid.Closed||raid.Spawner.GameEnd)throw new OperationCanceledException("Raid ended before safe reinforcement activation.");
            if(Safe(point,out var nearest,out var reason,entry.ProfileId))return nearest;
            if(Time.time>=entry.LastDeferral+10)
            {
                entry.LastDeferral=Time.time;
                ClientDiagnostics.Record("waves.safety_wait",new{ticket=entry.Ticket,stage,reason,nearestPlayerDistance=float.IsInfinity(nearest)?(float?)null:nearest});
            }
            await Task.Delay(500);
        }
    }
    internal static void Died(Player player)
    {
        var raid=Current;if(raid is null||raid.Closed||!Plugin.Infected(player))return;
        if(raid.Budget.Died(player.Profile.Id.ToString()))ClientDiagnostics.Record("waves.actor_dead",raid.Snapshot());
    }
    private static void Stop(BotSpawner __instance){if(Current?.Spawner==__instance){Close(Current);Current=null;}}
    private static void Close(Raid raid){raid.Closed=true;raid.Budget.End();raid.Spawner.OnBotCreated-=raid.Created;foreach(var entry in raid.Tickets.Values)entry.Deferred=null;ClientDiagnostics.Record("waves.end",raid.Snapshot());}
}
