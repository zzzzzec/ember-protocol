using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BepInEx;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Codex.ZombieSurvival.Rules;
namespace Codex.ZombieSurvival.Client;

internal static class RaidLoading
{
    private static JObject Settings=new();
    private static bool Enabled;
    private static readonly ConditionalWeakTable<BotsPresets,object> OpeningPlans=new();
    private static readonly Stopwatch Clock=new();
    private static string LoadId="";
    private static bool Active,PriorityLease;
    private static UnityEngine.ThreadPriority PreviousPriority;
    private static double PreviousPhase;
    internal static void Install(Harmony h)
    {
        try{Settings=JObject.Parse(File.ReadAllText(Path.Combine(BepInEx.Paths.PluginPath,"Codex-ZombieSurvival","loading-settings.json")));Enabled=(bool?)Settings["enabled"]==true;}
        catch(Exception e){Plugin.Log.LogWarning("[SPT-Z] Loading optimizations disabled: "+e.Message);}
        void Before(Type t,string method,string patch)=>h.Patch(AccessTools.Method(t,method),prefix:new HarmonyMethod(typeof(RaidLoading),patch));
        Before(typeof(TarkovApplication),"method_41",nameof(Begin));
        h.Patch(AccessTools.Method(typeof(TarkovApplication),"method_41"),postfix:new HarmonyMethod(typeof(RaidLoading),nameof(WatchLoad)));
        foreach(var name in new[]{"SetMatchingCompleted","SetLocationLoaded","SetGamePrepared","SetGameCreated","SetPlayerSpawnEvent","SetGamePooled"})
            h.Patch(AccessTools.Method(typeof(MetricsEventsClass),name),postfix:new HarmonyMethod(typeof(RaidLoading),nameof(Metric)));
        h.Patch(AccessTools.Method(typeof(MatchmakerFinalCountdown),"Show",new[]{typeof(Profile),typeof(DateTime)}),prefix:new HarmonyMethod(typeof(RaidLoading),nameof(Countdown)));
        Before(typeof(TarkovApplication),"LoadMainMenu",nameof(Menu));
        Before(typeof(TarkovApplication),"OnApplicationQuit",nameof(Quit));
        Before(typeof(GameWorld),"OnGameStarted",nameof(Started));
        h.Patch(AccessTools.Constructor(typeof(BotsPresets),new[]{typeof(ISession),typeof(BotWaveDataClass[]),typeof(BossLocationSpawn[]),typeof(WaveInfoClass[]),typeof(bool)}),postfix:new HarmonyMethod(typeof(RaidLoading),nameof(Plan)));
        h.Patch(AccessTools.Method(typeof(BotsPresets),"TryLoadBotsProfilesOnStart"),prefix:new HarmonyMethod(typeof(RaidLoading),nameof(Preload)),postfix:new HarmonyMethod(typeof(RaidLoading),nameof(WatchProfiles)));
    }
    private static void Begin()
    {
        Restore();LoadId=Guid.NewGuid().ToString("N").Substring(0,12);PreviousPhase=0;Clock.Restart();Active=true;
        PreviousPriority=Application.backgroundLoadingPriority;
        if(Enabled&&(bool?)Settings["highPriorityDuringLoading"]==true){PriorityLease=true;Application.backgroundLoadingPriority=UnityEngine.ThreadPriority.High;}
        Phase("deploy",new{enabled=Enabled,priorityBefore=PreviousPriority.ToString(),priority=Application.backgroundLoadingPriority.ToString()});
    }
    private static void Metric(MethodBase __originalMethod)=>Phase(__originalMethod.Name.Substring(3));
    private static void Phase(string phase,object? detail=null)
    {
        if(!Active)return;var seconds=Clock.Elapsed.TotalSeconds;
        ClientDiagnostics.Record("load.phase",new{load=LoadId,phase,seconds,stageSeconds=seconds-PreviousPhase,detail});PreviousPhase=seconds;
    }
    private static void Restore()
    {
        if(!PriorityLease)return;Application.backgroundLoadingPriority=PreviousPriority;PriorityLease=false;
        ClientDiagnostics.Record("load.priority_restored",new{load=LoadId,priority=Application.backgroundLoadingPriority.ToString()});
    }
    private static void End(string reason){if(Active)Phase(reason);Restore();Active=false;Clock.Stop();}
    private static void Countdown(){Phase("countdown");Restore();}
    private static void Started()=>End("started");
    private static void Menu()=>End("menu");
    private static void Quit()=>End("quit");
    private static void WatchLoad(ref Task __result)=>__result=Guard(__result);
    private static async Task Guard(Task task){try{await task;}catch(Exception e){Phase("failed",new{error=e.GetType().Name});End("aborted");throw;}}
    private static void Plan(BotsPresets __instance,BotWaveDataClass[] __1,BossLocationSpawn[] __2,WaveInfoClass[] __3)
    {
        ClientDiagnostics.Record("load.preload_inputs",new{load=LoadId,enabled=Enabled,finite=FiniteWaves.IsEnabled,regular=__1?.Length??0,bosses=__2?.Length??0,nonWave=__3?.Length??0});
        if(!Enabled||!FiniteWaves.IsEnabled||__2 is null||__2.Length==0||(__1?.Length??0)>0||__2.Any(w=>w.EscortCount!=0))return;
        var raw=__2.SelectMany(w=>w.GetUsingTypes()).Select(w=>new PreloadGroup(w.Role.ToString(),w.Difficulty.ToString(),w.Limit)).ToArray();
        var opening=LoadingPolicy.Opening(raw,raw.Sum(p=>p.Count));if(opening is null)return;
        __instance.List_2=opening.Select(p=>new WaveInfoClass(p.Count,(WildSpawnType)Enum.Parse(typeof(WildSpawnType),p.Role),(BotDifficulty)Enum.Parse(typeof(BotDifficulty),p.Difficulty))).ToList();
        OpeningPlans.Add(__instance,new object());
        ClientDiagnostics.Record("load.preload_plan",new{load=LoadId,planned=raw.Sum(p=>p.Count),preload=opening.Sum(p=>p.Count),unusedNonWaveEntries=__3?.Length??0,groups=opening});
    }
    private static bool Preload(BotsPresets __instance,out long __state,ref Task __result)
    {
        __state=Stopwatch.GetTimestamp();Phase("profiles_begin");
        if(!OpeningPlans.TryGetValue(__instance,out _))return true;
        OpeningPlans.Remove(__instance);__result=FixedProfiles(__instance);return false;
    }
    private static async Task FixedProfiles(BotsPresets presets)
    {
        // Prewarm every ticket in this finite raid, but no unused native crowd or
        // non-wave pools. Avoid concurrent lazy-cache misses during later batches.
        // Preserve the native 100-profile request ceiling and the native cache writer.
        var groups=presets.List_2.Select(w=>new PreloadGroup(w.Role.ToString(),w.Difficulty.ToString(),w.Limit)).ToArray();
        presets.Bool_0=false;
        foreach(var batch in LoadingPolicy.Batches(groups,100))
            await presets.method_2(batch.Select(p=>new WaveInfoClass(p.Count,(WildSpawnType)Enum.Parse(typeof(WildSpawnType),p.Role),(BotDifficulty)Enum.Parse(typeof(BotDifficulty),p.Difficulty))).ToList(),EProfilesAskingStat.external);
        presets.Bool_0=true;
    }
    private static void WatchProfiles(ref Task __result,long __state)=>__result=Profiles(__result,__state);
    private static async Task Profiles(Task task,long start)
    {
        await task;Phase("profiles_ready",new{durationSeconds=(Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency});
    }
}
