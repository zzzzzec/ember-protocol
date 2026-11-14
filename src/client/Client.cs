using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using Codex.ZombieSurvival.Rules;

namespace Codex.ZombieSurvival.Client;

[BepInPlugin("codex.zombie-survival.client","SPT-Z Survival","2.21.6")]
[BepInDependency("com.netVnum.pause", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin:BaseUnityPlugin
{
    internal static ManualLogSource Log=null!;
    internal static readonly HearingSettings Hearing=new();
    internal static readonly PatrolSettings Patrol=new();
    internal static bool CustomsIdleBudget;
    internal static bool CustomsStaggeredResponse = true;
    internal static float LoudMin=>Hearing.LoudMin;
    internal static float LoudMax=>Hearing.LoudMax;
    internal static float QuietMax=>Hearing.QuietMax;
    internal static float ExplosionRange=>Hearing.ExplosionRange;
    internal static float FootstepRange=>Hearing.FootstepRange;
    private void Awake()
    {
        Log=Logger;
        Hearing.LoudMin=Mathf.Clamp(Config.Bind("Hearing","UnsuppressedMinRange",25f,"Minimum unsuppressed attraction radius in metres, before map scaling.").Value,10,100);
        Hearing.LoudMax=Mathf.Clamp(Config.Bind("Hearing","UnsuppressedMaxRange",80f,"Maximum unsuppressed attraction radius before map scaling.").Value,Hearing.LoudMin,160);
        Hearing.QuietMax=Mathf.Clamp(Config.Bind("Hearing","SuppressedMaxRange",20f,"Maximum suppressed attraction radius before map scaling.").Value,4,Hearing.LoudMin);
        Hearing.ExplosionRange=Mathf.Clamp(Config.Bind("Hearing","ExplosionRange",120f,"Explosion attraction radius before map scaling.").Value,20,200);
        Hearing.FootstepRange=Mathf.Clamp(Config.Bind("Hearing","FootstepMaxRange",8f,"Maximum player movement attraction radius before map scaling.").Value,1,20);
        Hearing.GroundZeroScale=Mathf.Clamp(Config.Bind("Hearing","GroundZeroRangeScale",.75f,"Hearing radius multiplier for both Ground Zero map variants.").Value,.25f,1);
        Hearing.InfectedShotScale=Mathf.Clamp(Config.Bind("Hearing","InfectedGunshotRangeScale",.45f,"Armed infected shots attract a smaller local crowd.").Value,.1f,1);
        Hearing.WallScale=Mathf.Clamp(Config.Bind("Hearing","WallRangeScale",.35f,"Remaining hearing radius through a wall.").Value,.1f,1);
        Hearing.FloorScale=Mathf.Clamp(Config.Bind("Hearing","FloorRangeScale",.55f,"Additional range multiplier beyond three metres of height difference.").Value,.1f,1);
        Hearing.CallRange=Mathf.Clamp(Config.Bind("Hearing","NativeCallMaxRange",30f,"Local limit for native infected pursuit broadcasts.").Value,10,60);
        Patrol.Radius=PatrolPolicy.Setting(Config.Bind("Patrol","IdleWanderRadius",24f,"Radius around an infected actor's local home for idle destination selection, in metres.").Value,8,45,24);
        Patrol.Spacing=PatrolPolicy.Setting(Config.Bind("Patrol","PreferredIdleSpacing",6f,"Soft spacing between idle destinations; never applied to sound or sight investigations.").Value,2,12,6);
        Patrol.HoldMin=PatrolPolicy.Setting(Config.Bind("Patrol","MinimumIdleHold",10f,"Minimum idle hold before choosing a new local destination, in seconds.").Value,5,30,10);
        Patrol.HoldMax=PatrolPolicy.Setting(Config.Bind("Patrol","MaximumIdleHold",24f,"Maximum idle hold; actor-specific timers avoid synchronized movement.").Value,Patrol.HoldMin,60,Math.Max(24,Patrol.HoldMin));
        CustomsIdleBudget=Config.Bind("Performance","CustomsIdleBudget",true,"Pause heavy AI decisions for distant, covered and unalerted Customs infected. Models and hitboxes remain active.").Value;
        var h=new Harmony("codex.spt-z.client");
        h.Patch(AccessTools.Method(typeof(GameWorld),"OnGameStarted"),postfix:new HarmonyMethod(typeof(Plugin),nameof(Started)));
        h.Patch(AccessTools.Method(typeof(Player),"OnDead"),postfix:new HarmonyMethod(typeof(Plugin),nameof(Died)));
        h.Patch(typeof(MerchantsList).GetMethods().Single(m=>m.Name=="Show"&&m.GetParameters().Length==7),prefix:new HarmonyMethod(typeof(Plugin),nameof(Merchants)));
        h.Patch(typeof(TraderDealScreen).GetMethods().Single(m=>m.Name=="Show"&&m.GetParameters().Length==7),prefix:new HarmonyMethod(typeof(Plugin),nameof(DealList)));
        h.Patch(AccessTools.Method(typeof(TraderScreensGroup),"Show"),prefix:new HarmonyMethod(typeof(Plugin),nameof(GroupList)));
        SurvivalUi.Install(h);
        MapAbundance.Install(h);
        ThreatActors.Install(h);
        BoundedHearing.Install(h);
        InfectedAwareness.Install(h);
        InfectedPatrol.Install(h);
        InfectedTurning.Install(h);
        InfectedWeaponActivation.Install(h);
        InfectedIdleBudget.Install(h);
        FiniteWaves.Install(h);
        RaidLoading.Install(h);
        ClientDiagnostics.Install(h);
        var stopSession = AccessTools.DeclaredMethod(typeof(BaseLocalGame<EftGamePlayerOwner>),
            nameof(BaseLocalGame<EftGamePlayerOwner>.StopSession), new[] { typeof(string), typeof(ExitStatus), typeof(string) });
        h.Patch(stopSession, prefix: new HarmonyMethod(typeof(Plugin), nameof(StopLocalSession)));
        LabBlackout.Install(h);
        SkillProgression.Install(h);
        CombatSkillPractice.Install(h);
        InfectedAudio.Install(h, Config);
        PauseCompatibility.Install();
        try{ClientDiagnostics.Start();}catch(Exception e){Log.LogWarning("SPT-Z diagnostics could not start: "+e.Message);}
        Log.LogInfo("SPT-Z ready: independent idle patrol, local sound/sight convergence, unlocked doors and sealed infected corpses.");
    }
    private void Start()
    {
        var rows=Harmony.GetAllPatchedMethods().Where(m=>m.DeclaringType?.FullName?.StartsWith("EFT.LocalPlayer")==true||m.DeclaringType?.FullName?.StartsWith("EFT.TarkovApplication")==true||m.DeclaringType==typeof(BotsPresets)||m.DeclaringType==typeof(GClass680)||m.DeclaringType==typeof(BotCreationDataClass)||m.DeclaringType==typeof(BotCreatorClass))
            .Select(m=>{var p=Harmony.GetPatchInfo(m);return m.DeclaringType!.FullName+"::"+m.Name+" => "+string.Join("; ",p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers).Select(x=>x.owner+":"+x.PatchMethod.DeclaringType?.FullName+"."+x.PatchMethod.Name));});
        File.WriteAllLines(Path.Combine(BepInEx.Paths.PluginPath,"Codex-ZombieSurvival","harmony-creation-patches.txt"),rows);
    }
    // The exit trigger also reports AI exits. SPT snapshots (and can clear)
    // the local profile in LocalGame.Stop before the game's own ID check.
    private static bool StopLocalSession(BaseLocalGame<EftGamePlayerOwner> __instance,
        string __0, ExitStatus __1, string __2)
    {
        if (__instance is not LocalGame game || game.Profile_0 is not { } profile || profile.Id == __0)
            return true;

        ClientDiagnostics.Record("raid.foreign_exit_ignored", new
        {
            actor = ClientDiagnostics.Key(__0), status = __1.ToString(), exit = __2
        });
        return false;
    }

    private const string Quartermaster="4b7e8889344316d03e8369b7";
    internal static bool ManagedTrader(string id)=>id is Quartermaster or "579dc571d53a0658a154fbec" or "09ce24e1c055c45828a1922f" or "39a130d714ed6d61f6b1c2aa" or "428880b53be6cbb14b9e2f11" or "afee5b874a94cd243d05ff44" or "d20d87e0da4822e5261cd161";
    public static bool TraderVisible(TraderClass trader)
        => trader.Settings.Id == Quartermaster || trader.Settings.Id == "579dc571d53a0658a154fbec"
           || ManagedTrader(trader.Settings.Id) && trader.Info.Unlocked && !trader.Info.Disabled;
    private static IEnumerable<TraderClass> Visible(IEnumerable<TraderClass> traders)=>traders.Where(TraderVisible).ToArray();
    private static void Merchants(ref IEnumerable<TraderClass> __0){__0=Visible(__0);}
    private static void DealList(ref IEnumerable<TraderClass> __6){__6=Visible(__6);}
    private static void GroupList(object __0)
    {
        var field=AccessTools.Field(__0.GetType(),"TradersList");
        if(field.GetValue(__0) is IEnumerable<TraderClass> list)field.SetValue(__0,Visible(list));
    }
    internal static bool Infected(Player p)=>p?.Profile?.Info?.Settings?.Role.ToString().StartsWith("infected",StringComparison.OrdinalIgnoreCase)==true;
    private static void Started()
    {
        if(!Singleton<GameWorld>.Instantiated)return;
        var world=Singleton<GameWorld>.Instance;
        world.gameObject.AddComponent<SurvivalRaid>();
        LabBlackout.Begin(world);
    }
    private static void Died(Player __instance,EDamageType __0)
    {
        ClientDiagnostics.Death(__instance,__0);
        FiniteWaves.Died(__instance);
        if(Infected(__instance)&&AccessTools.Field(typeof(Player),"Corpse")?.GetValue(__instance) is Corpse corpse)
        {
            corpse.IsZombieCorpse=true;
            ClientDiagnostics.Record("corpse.sealed",new {id=ClientDiagnostics.Key(__instance.Profile.Id)});
        }
    }
}

internal static class SurvivalUi
{
    private const string Quartermaster="4b7e8889344316d03e8369b7";

    // These two trader layouts intentionally have different optional fields.
    // Cache absence quietly instead of asking Harmony to warn on every UI frame.
    private static readonly Dictionary<Type,Dictionary<string,FieldInfo?>> UiFields=new();
    private static FieldInfo? UiField(Type type,string name)
    {
        if(!UiFields.TryGetValue(type,out var fields)){fields=new();UiFields.Add(type,fields);}
        if(fields.TryGetValue(name,out var cached))return cached;
        FieldInfo? found=null;
        for(var owner=type;owner is not null&&found is null;owner=owner.BaseType)
            found=owner.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
        fields[name]=found;return found;
    }
    private static void LabelStyle(object label,float minimum,float maximum,bool singleLine=false)
    {
        var type=label.GetType();
        AccessTools.Property(type,"enableAutoSizing")?.SetValue(label,true);
        AccessTools.Property(type,"fontSizeMin")?.SetValue(label,minimum);
        AccessTools.Property(type,"fontSizeMax")?.SetValue(label,maximum);
        if(singleLine)AccessTools.Property(type,"enableWordWrapping")?.SetValue(label,false);
    }
    private static void CompactQuestCounter(object __instance)
    {
        if(UiField(__instance.GetType(),"_questsCounterText")?.GetValue(__instance) is { } label)LabelStyle(label,8,16,true);
    }

    internal static void Install(Harmony h)
    {
        void Post(Type t,string m,string p)=>h.Patch(AccessTools.Method(t,m),postfix:new HarmonyMethod(typeof(SurvivalUi),p));
        Post(typeof(MenuTaskBar),"Awake",nameof(HideTaskBar));
        Post(typeof(MenuTaskBar),"SetCustomButtonsAvailability",nameof(HideTaskBar));
        Post(typeof(MenuTaskBar),"SetButtonsInteractable",nameof(HideTaskBar));
        Post(typeof(TradingScreen),"Awake",nameof(HideTradingTab));
        h.Patch(typeof(TradingScreen).GetMethods().Single(m=>m.Name=="Show"&&m.GetParameters().Length==8),
            prefix:new HarmonyMethod(typeof(SurvivalUi),nameof(MerchantsTab)),postfix:new HarmonyMethod(typeof(SurvivalUi),nameof(HideTradingTab)));
        h.Patch(typeof(EFT.UI.Ragfair.RagfairScreen).GetMethods().Single(m=>m.Name=="Show"&&m.GetParameters().Length==6),prefix:new HarmonyMethod(typeof(SurvivalUi),nameof(NoFlea)));
        var interactions=typeof(ItemInfoInteractionsAbstractClass<EItemInfoButton>);
        Post(interactions,"get_AllInteractions",nameof(FilterInteractions));
        Post(interactions,"get_SubInteractions",nameof(FilterInteractions));
        h.Patch(AccessTools.Method(interactions,"ExecuteInteraction"),prefix:new HarmonyMethod(typeof(SurvivalUi),nameof(NoFleaInteraction)));
        var contextShow=typeof(InteractionButtonsContainer).GetMethods().Single(m=>m.Name=="Show"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==6).MakeGenericMethod(typeof(EItemInfoButton));
        h.Patch(contextShow,prefix:new HarmonyMethod(typeof(SurvivalUi),nameof(ProhibitFleaButtons)));
        Post(typeof(QuestsListView),"Show",nameof(CompactQuestCounter));
        Post(typeof(TraderPanel),"method_1",nameof(Standing));
        Post(typeof(TraderPanel),"Show",nameof(Standing));
        h.Patch(AccessTools.Method(typeof(TraderPanel),"Update"),prefix:new HarmonyMethod(typeof(SurvivalUi),nameof(RaidSupplyClock)));
        h.Patch(AccessTools.Method(typeof(TraderCard),"LateUpdate"),prefix:new HarmonyMethod(typeof(SurvivalUi),nameof(RaidSupplyClock)));
        Post(typeof(TraderCard),"Show",nameof(CardShown));
        Post(typeof(TraderCard),"method_0",nameof(Standing));
        Post(typeof(TraderPanel),"method_0",nameof(Standing));
        Post(typeof(TraderPanel),"method_2",nameof(Standing));
        h.Patch(AccessTools.Method(typeof(InfectionEffect),"OnRenderImage"),prefix:new HarmonyMethod(typeof(SurvivalUi),nameof(NoInfectionOverlay)));
        h.Patch(typeof(EFT.UI.Matchmaker.MatchMakerSideSelectionScreen).GetMethods().Single(m=>m.Name=="Show"&&m.GetParameters().Length==4),postfix:new HarmonyMethod(typeof(SurvivalUi),nameof(PmcCampaignOnly)));
    }
    private static void Hide(object? target)
    {
        if(target is GameObject go)go.SetActive(false);
        else if(target is Component c)c.gameObject.SetActive(false);
    }
    private static void PmcCampaignOnly(object __instance)
    {
        foreach(var field in new[]{"_savagesButton","_savagesBigButton","_savageModelView","_savageBlocker","_savageBlockMessage","_randomButton"})
            Hide(UiField(__instance.GetType(),field)?.GetValue(__instance));
    }
    private static void HideTaskBar(object __instance)
    {
        foreach(var f in new[]{"_hoverTooltipAreas","_toggleButtons"})
            if(UiField(__instance.GetType(),f)?.GetValue(__instance) is System.Collections.IDictionary list&&list.Contains(EMenuType.RagFair))Hide(list[EMenuType.RagFair]);
    }
    private static void HideTradingTab(object __instance)
    {
        foreach(var f in new[]{"_ragfairToggle","_ragfairLockIcon"})Hide(UiField(__instance.GetType(),f)?.GetValue(__instance));
    }
    private static void MerchantsTab(object[] __args){__args[7]=Enum.ToObject(__args[7].GetType(),1);}
    private static bool NoFlea(object __instance){Hide(__instance);return false;}
    private static bool NoInfectionOverlay(InfectionEffect __instance,RenderTexture __0,RenderTexture __1){ClientDiagnostics.Infection(__instance.EffectValue);Graphics.Blit(__0,__1);return false;}
    private static bool Allowed(EItemInfoButton x)=>x is not (EItemInfoButton.FilterSearch or EItemInfoButton.LinkedSearch or EItemInfoButton.NeededSearch or EItemInfoButton.AddOffer);
    private static void ProhibitFleaButtons(ref List<EItemInfoButton> __2)
    {
        __2=(__2??new List<EItemInfoButton>()).Concat(new[]{EItemInfoButton.FilterSearch,EItemInfoButton.LinkedSearch,EItemInfoButton.NeededSearch,EItemInfoButton.AddOffer}).Distinct().ToList();
        ClientDiagnostics.Record("ui.context_filtered",new {hidden=__2.Select(x=>x.ToString()).ToArray()});
    }
    private static void FilterInteractions(ref IEnumerable<EItemInfoButton> __result){if(__result is not null)__result=__result.Where(Allowed).ToArray();}
    private static bool NoFleaInteraction(EItemInfoButton __0,ref bool __result){if(Allowed(__0))return true;__result=false;return false;}
    private static bool RaidSupplyClock(object __instance)
    {
        if(UiField(__instance.GetType(),"traderInfo_0")?.GetValue(__instance) is not Profile.TraderInfo info||!Plugin.ManagedTrader(info.Id)||info.NextResupply!=int.MaxValue)return true;
        if(UiField(__instance.GetType(),"_timeGroup")?.GetValue(__instance) is GameObject group)group.SetActive(true);
        Hide(UiField(__instance.GetType(),"_restockedGroup")?.GetValue(__instance));
        var label=UiField(__instance.GetType(),"_timeLeft")?.GetValue(__instance);
        if(label is not null)
        {
            var type=label.GetType();
            var text=AccessTools.Property(type,"text");
            if(text?.GetValue(label)?.ToString()!="战局后补货")
            {LabelStyle(label,8,14,true);text?.SetValue(label,"战局后补货");}
        }
        return false;
    }
    private static void CardShown(object __instance){Standing(__instance);RaidSupplyClock(__instance);}
    private static void Standing(object __instance)
    {
        if(UiField(__instance.GetType(),"traderInfo_0")?.GetValue(__instance) is not Profile.TraderInfo info||!Plugin.ManagedTrader(info.Id))return;
        var label=UiField(__instance.GetType(),"_standing")?.GetValue(__instance);
        if(label is null)return;
        var tier=Math.Max(1,Math.Min(4,info.LoyaltyLevel));
        var levels=info.Settings?.LoyaltyLevels;
        var required=levels is not null&&tier<levels.Length?levels[tier].MinStanding:new[]{.20,.50,.90,.90}[tier-1];
        var text=info.Id=="579dc571d53a0658a154fbec"?(info.Standing<6?$"信誉 {info.Standing:F2}/6.00":$"熟客 {info.Standing:F2}"):tier<4?$"好感 {info.Standing:F2}/{required:F2}":$"好感 {info.Standing:F2} · 满级";
        var type=label.GetType();
        AccessTools.Property(type,"enableAutoSizing")?.SetValue(label,true);
        AccessTools.Property(type,"fontSizeMin")?.SetValue(label,9f);
        AccessTools.Property(type,"fontSizeMax")?.SetValue(label,16f);
        AccessTools.Property(type,"text")?.SetValue(label,text);
    }
}

public sealed partial class SurvivalRaid:MonoBehaviour
{
    internal static SurvivalRaid? Current {get;private set;}
    internal bool GroundZero {get;private set;}
    internal bool Customs {get;private set;}
    private int _nativeHearingBlocks,_nativeCallBlocks,_nativeEnemies,_visibleEnemies,_distantEnemies,_investigating;
    internal void NativeHearingRejected()=>_nativeHearingBlocks++;
    internal void NativeCallRejected()=>_nativeCallBlocks++;
    private GameWorld _world=null!;
    private Door[] _doors=Array.Empty<Door>();
    private readonly List<Player> _infected=new();
    private readonly Dictionary<Player,float> _swing=new(),_open=new();
    private readonly Dictionary<Player,(float Due,int Attempts)> _activationRetries=new();
    private float _nextActivationRetry;
    private sealed class Attention { public Vector3 Point; public float Until, ObservedAt; public string Reason="unknown"; public bool PlayerGunfire; }
    private sealed class Noise { public Vector3 Point; public float Radius,Duration,Power; public string Kind="",Source=""; }
    private readonly Dictionary<Player,Attention> _attention=new();
    private readonly ConcurrentQueue<Noise> _noises=new();
    private BotEventHandler? _events;
    private float _nextTick;
    private bool _reportedError;
    private int _spawned,_doorOpens,_swings;
    private float _nextReport;
    private int _loudEvents,_quietEvents,_stepEvents,_blastEvents,_loudAlerts,_quietAlerts;
    private Vector3 _customsBurstOrigin;
    private readonly Dictionary<Player,(float Due, Vector3 Point, int Sector)> _customsResponses=new();
    private readonly List<Player> _finishedCustomsResponses=new();
    private int _customsResponseCount, _customsApproachMask;
    private float _customsGunfireUntil;
    private int _customsGunfireShots;
    private string _lastNoise="none";
    private float _lastRange;
    private Func<object> _diagnosticCombat=null!;
    private static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    private void Start()
    {
        _world=Singleton<GameWorld>.Instance;Current=this;ThreatActors.Reset();InfectedTurning.Reset();
        var map=_world.MainPlayer?.Location??_world.LocationId;
        GroundZero=HearingPolicy.IsGroundZero(map);
        Customs=string.Equals(map,"bigmap",StringComparison.OrdinalIgnoreCase);
        _diagnosticCombat=()=>new {spawned=_spawned,alive=_infected.Count(p=>p!=null&&p.HealthController?.IsAlive==true),doorOpens=_doorOpens,swings=_swings,loudEvents=_loudEvents,quietEvents=_quietEvents,stepEvents=_stepEvents,blastEvents=_blastEvents,loudAlerts=_loudAlerts,quietAlerts=_quietAlerts,lastNoise=_lastNoise,lastRange=_lastRange,nativeActionError=_reportedError,nativeHearingBlocked=_nativeHearingBlocks,nativeCallsBlocked=_nativeCallBlocks,nativeEnemies=_nativeEnemies,visibleEnemies=_visibleEnemies,distantNativeEnemies=_distantEnemies,investigating=_investigating,awareness=AwarenessDiagnostic(),idleBudget=IdleBudgetDiagnostic(),waves=FiniteWaves.Diagnostic(),customsResponse=Customs ? new { staggered=Plugin.CustomsStaggeredResponse, pending=_customsResponses.Count, released=_customsResponseCount, approaches=_customsApproachMask } : null};
        try{ClientDiagnostics.BeginRaid(_world);}catch(Exception e){ClientDiagnostics.Record("raid.begin_capture_error",new {error=e.ToString()});}
        CombatSkillPractice.Begin(_world);
        _doors=FindObjectsOfType<Door>();
        if (Customs) RestoreCheckpointKey();
        _world.OnPersonAdd+=Added;
        if(Singleton<BotEventHandler>.Instantiated)
        {
            _events=Singleton<BotEventHandler>.Instance;
            _events.OnSoundPlayed+=Sound;
            _events.OnGrenadeExplosive+=Explosion;
        }
        foreach(var p in _world.AllAlivePlayersList)Added(p);
        Plugin.Log.LogInfo("[SPT-Z] Raid initialized. Hearing uses native gun/silencedGun/step and explosion events; investigation follows the last heard position.");
    }

    private void RestoreCheckpointKey()
    {
        // The Customs checkpoint's outer door ships locked with an empty KeyId in EFT 0.16.9.40087.
        var door = _doors.FirstOrDefault(candidate => candidate != null &&
            candidate.Id == "door_custom_multiScene_00000" && candidate.gameObject.scene.name == "custom_multiScene" &&
            candidate.transform.parent?.parent?.name == "KPP");
        if (door == null || !string.IsNullOrEmpty(door.KeyId)) return;
        door.KeyId = "5913915886f774123603c392";
        ClientDiagnostics.Record("door.checkpoint_key_restored", new { door = door.Id, state = door.DoorState.ToString() });
    }

    private void Sound(IPlayer source,Vector3 point,float power,AISoundType type)
    {
        var kind=BoundedHearing.Kind(type);
        if(kind.Length==0||(kind=="step"&&(source==null||source.IsAI)))return;
        var infected=BoundedHearing.InfectedSource(source);
        var radius=SoundRadius(kind,power,infected,source is {IsAI:false});
        if(radius<=0)return;
        _noises.Enqueue(new Noise{Point=point,Radius=radius,Duration=HearingPolicy.MemorySeconds(kind),Power=power,Kind=kind,Source=source==null?"unknown":source.IsAI?(infected?"infected":"other_ai"):"player"});
    }
    internal float SoundRadius(string kind,float power,bool infected,bool human)
    {
        var radius=HearingPolicy.Radius(Plugin.Hearing,kind,power,GroundZero,infected);
        return Customs&&human&&kind=="gun"?radius*1.25f:radius;
    }
    internal float CallRadius=>Plugin.Hearing.CallRange*(GroundZero?Plugin.Hearing.GroundZeroScale:Customs?1.45f:1);
    private void Explosion(Vector3 point,string profileId,bool smoke,float smokeRadius,float lifetime,int sourceId)
    {
        if(!smoke)_noises.Enqueue(new Noise{Point=point,Radius=HearingPolicy.Radius(Plugin.Hearing,"explosion",1,GroundZero,false),Duration=HearingPolicy.MemorySeconds("explosion"),Power=1,Kind="explosion",Source="explosion"});
    }
    internal static bool Occluded(Vector3 from,Vector3 to)=>Physics.Linecast(from,to,LayerMaskClass.HighPolyWithTerrainMaskAI,QueryTriggerInteraction.Ignore);
    private void HearNoises()
    {
        for(var count=0;count<128&&_noises.TryDequeue(out var noise);count++)
        {
            var playerGunfire = noise.Source == "player" && (noise.Kind is "gun" or "silencedGun");
            var radius=noise.Radius;
            var minimumWallScale=0f;
            var pressure=0;
            if(Customs&&noise.Kind=="gun"&&noise.Source=="player")
            {
                var sameArea=HearingPolicy.ContinueCustomsBurst(Time.time,_customsGunfireUntil,(noise.Point-_customsBurstOrigin).sqrMagnitude);
                if(!sameArea)
                {
                    _customsBurstOrigin=noise.Point;
                    _customsResponses.Clear();
                }
                pressure=_customsGunfireShots=sameArea?Mathf.Min(_customsGunfireShots+1,3):1;
                if(!Plugin.CustomsStaggeredResponse)_customsBurstOrigin=noise.Point;
                _customsGunfireUntil=Time.time+8;
                (radius,minimumWallScale)=HearingPolicy.CustomsGunfire(radius,pressure);
            }
            _lastNoise=noise.Kind;_lastRange=radius;
            if(noise.Kind=="gun")_loudEvents++;else if(noise.Kind=="silencedGun")_quietEvents++;else if(noise.Kind=="step")_stepEvents++;else _blastEvents++;
            var staggered=Plugin.CustomsStaggeredResponse&&pressure>0;
            var heard=0;var alerted=0;var newlyAlerted=0;var attenuated=0;var farthest=0f;
            foreach(var z in _infected)
            {
                if(z==null||z.HealthController?.IsAlive!=true)continue;
                var pos=z.Transform.position;var distance=Vector3.Distance(pos,noise.Point);
                var audible=ThreatActors.Hearing(z,radius);
                if(distance>audible)continue;
                var wallScale=staggered
                    ? HearingPolicy.CustomsWallResponse(minimumWallScale,pressure,pos.y-noise.Point.y)
                    : minimumWallScale;
                var effective=BoundedHearing.Effective(pos,noise.Point,audible,wallScale);
                if(distance>effective){attenuated++;continue;}
                heard++;
                var active=_attention.TryGetValue(z,out var old)&&old.Until>Time.time;
                if(staggered)
                {
                    if(z.AIData?.BotOwner?.Memory?.GoalEnemy?.IsVisible==true)continue;
                    if(!active || old!.Reason!="gun" || !old.PlayerGunfire)
                    {
                        var offset=pos-_customsBurstOrigin;
                        var sector=HearingPolicy.ApproachSector(offset.x,offset.z);
                        var delay=HearingPolicy.CustomsResponseDelay(distance,noise.Radius,pressure,sector,(z.Id&15)/15f);
                        if(delay<0)continue;
                        if(delay>0)
                        {
                            var due=Time.time+delay;
                            if(_customsResponses.TryGetValue(z,out var queued))due=Mathf.Min(due,queued.Due);
                            _customsResponses[z]=(due,noise.Point,sector);
                            continue;
                        }
                    }
                    _customsResponses.Remove(z);
                }
                if(noise.Kind=="step"&&active&&old!.Until>Time.time+5)continue;
                var reason=noise.Source=="infected"?"infected_"+noise.Kind:noise.Kind;
                if(!InvestigateAt(z,noise.Point,noise.Duration,reason,playerGunfire))continue;
                if(!active)newlyAlerted++;
                alerted++;farthest=Mathf.Max(farthest,distance);
            }
            if(noise.Kind=="gun")_loudAlerts+=alerted;else if(noise.Kind=="silencedGun")_quietAlerts+=alerted;
            if(noise.Kind!="step")
            {
                Plugin.Log.LogInfo($"[SPT-Z] Hearing {noise.Kind} source={noise.Source} power={noise.Power:F1} range={radius:F1}m pressure={pressure} alerted={alerted} new={newlyAlerted} blocked={attenuated}");
                ClientDiagnostics.Record("hearing.event",new{kind=noise.Kind,source=noise.Source,power=noise.Power,radius,pressure,heard,alerted,newlyAlerted,refreshed=alerted-newlyAlerted,blocked=attenuated,farthestAlert=farthest,activeInfected=_infected.Count(z=>z!=null&&z.HealthController?.IsAlive==true),groundZero=GroundZero});
            }
        }
    }
    private void ReleaseCustomsResponses()
    {
        if(_customsResponses.Count==0)return;
        if(!Plugin.CustomsStaggeredResponse || Time.time>=_customsGunfireUntil)
        {
            _customsResponses.Clear();
            return;
        }
        foreach(var entry in _customsResponses)
        {
            var (due, point, sector)=entry.Value;
            if(Time.time<due)continue;
            _finishedCustomsResponses.Add(entry.Key);
            var actor=entry.Key;
            if(actor==null || actor.HealthController?.IsAlive!=true || actor.AIData?.BotOwner?.Memory?.GoalEnemy?.IsVisible==true)continue;
            if(!InvestigateAt(actor,point,HearingPolicy.MemorySeconds("gun"),"gun",playerGunfire:true))continue;
            _customsResponseCount++;
            _customsApproachMask|=1<<sector;
            ClientDiagnostics.Record("awareness.customs_response",new { sector, distance=Vector3.Distance(actor.Transform.position,point), id=ClientDiagnostics.Key(actor.Profile.Id) });
        }
        foreach(var actor in _finishedCustomsResponses)_customsResponses.Remove(actor);
        _finishedCustomsResponses.Clear();
    }
    internal void Added(IPlayer p)
    {
        if(p is not Player player||player.IsYourPlayer||!player.IsAI)return;
        if(player.Profile?.Info?.Settings?.Role.ToString().StartsWith("infected",StringComparison.OrdinalIgnoreCase)!=true)return;
        player.Profile.Info.Side=EPlayerSide.Savage;ThreatActors.Register(player);
        if(player.MovementContext!=null)InfectedTurning.Register(player.MovementContext);
        if(!_infected.Contains(player)){_infected.Add(player);_spawned++;ClientDiagnostics.Record("bot.spawn",new {id=ClientDiagnostics.Key(player.Profile.Id),role=player.Profile.Info.Settings.Role.ToString(),difficulty=player.Profile.Info.Settings.BotDifficulty,position=new {player.Transform.position.x,player.Transform.position.y,player.Transform.position.z}});}
    }
    private static object? Get(object? o,string name)=>o==null?null:o.GetType().GetProperty(name,Flags)?.GetValue(o)??o.GetType().GetField(name,Flags)?.GetValue(o);
    private static void VectorCall(object o,string name,Vector3 point)
    {
        var methods=o.GetType().GetMethods(Flags).Where(m=>m.Name==name&&m.GetParameters().Length>0&&m.GetParameters()[0].ParameterType==typeof(Vector3));
        var method=methods.OrderBy(m=>m.GetParameters().Length).FirstOrDefault();
        if(method==null)throw new MissingMethodException(o.GetType().Name,name);
        var ps=method.GetParameters();var args=new object?[ps.Length];args[0]=point;
        for(var i=1;i<ps.Length;i++)args[i]=ps[i].HasDefaultValue?ps[i].DefaultValue:ps[i].ParameterType==typeof(bool)?true:ps[i].ParameterType==typeof(float)?-1f:ps[i].ParameterType.IsValueType?Activator.CreateInstance(ps[i].ParameterType):null;
        method.Invoke(o,args);
    }
    private void Update()
    {
        var started=System.Diagnostics.Stopwatch.GetTimestamp();
        try{TickRaid();}
        finally{ClientDiagnostics.MeasureUpdate(System.Diagnostics.Stopwatch.GetTimestamp()-started);}
    }
    private bool WaitForInfectedActivation(Player player,BotOwner owner)
    {
        if(owner.BotState!=EBotState.PreActive)
        {
            if(_activationRetries.Remove(player,out var previous)&&previous.Attempts>0)
                ClientDiagnostics.Record("bot.activation_result",new {id=ClientDiagnostics.Key(player.Profile.Id),previous.Attempts,state=owner.BotState.ToString()});
            return false;
        }

        if(owner.WeaponManager is not {IsReady:false,Selector:{ } selector})return true;
        var now=Time.time;
        if(!_activationRetries.TryGetValue(player,out var retry))
        {
            _activationRetries[player]=(now+2,0);
            return true;
        }
        if(now<retry.Due||now<_nextActivationRetry)return true;
        var id=ClientDiagnostics.Key(player.Profile.Id);
        if(retry.Attempts>=3)
        {
            _activationRetries[player]=(float.PositiveInfinity,retry.Attempts);
            ClientDiagnostics.Record("bot.activation_exhausted",new {id,retry.Attempts});
            return true;
        }
        // PreActive bots advance only after the native weapon manager is ready.
        // Retry through the original selector, staggered to let shared pools catch up.
        var attempt=retry.Attempts+1;
        _activationRetries[player]=(now+2,attempt);
        _nextActivationRetry=now+.5f;
        ClientDiagnostics.Record("bot.activation_retry",new {id,attempt});
        InfectedWeaponActivation.Retry(player,selector);
        return true;
    }

    private void TickRaid()
    {
        if(_world==null)return;
        ClientDiagnostics.Tick(_world,_diagnosticCombat);
        if(Time.time<_nextTick)return;_nextTick=Time.time+.20f;
        FiniteWaves.Tick();
        HearNoises();
        ReleaseCustomsResponses();
        SamplePatrol();
        if(Time.time>=_nextReport)
        {
            _nextReport=Time.time+5;
            var path=Path.Combine(BepInEx.Paths.PluginPath,"Codex-ZombieSurvival","raid-diagnostics.txt");
            File.WriteAllText(path,"Spawned="+_spawned+"\nAlive="+_infected.Count(p=>p!=null&&p.HealthController?.IsAlive==true)+"\nDoorOpens="+_doorOpens+"\nMeleeSwings="+_swings+"\nDoors="+_doors.Length+"\nLoudShotEvents="+_loudEvents+"\nSuppressedShotEvents="+_quietEvents+"\nMovementSoundEvents="+_stepEvents+"\nExplosionEvents="+_blastEvents+"\nLoudShotAlerts="+_loudAlerts+"\nSuppressedShotAlerts="+_quietAlerts+"\nLastNoise="+_lastNoise+"\nLastNoiseRange="+_lastRange.ToString("F1")+"\nNativeActionError="+_reportedError+"\nUpdatedUtc="+DateTime.UtcNow.ToString("O"),Encoding.UTF8);
        }
        foreach(var dead in _infected.Where(p=>p==null||p.HealthController?.IsAlive!=true).ToArray())
        {_infected.Remove(dead);ForgetIdleActor(dead);if(dead!=null){_activationRetries.Remove(dead);_swing.Remove(dead);_open.Remove(dead);_attention.Remove(dead);_nextSightingReport.Remove(dead);ForgetPatrol(dead);}}
        var humans=_world.AllAlivePlayersList.OfType<Player>().Where(p=>!p.IsAI&&p.HealthController?.IsAlive==true).ToArray();
        if(humans.Length==0)return;
        _nativeEnemies=0;_visibleEnemies=0;_distantEnemies=0;_investigating=0;
        _near25=0;_near50=0;_near100=0;_sameFloorNear25=0;_localResponders=0;_localChasers=0;
        foreach(var z in _infected)
        {
            try
            {
                var pos=z.Transform.position;
                var target=humans[0];
                var distanceSquared=(pos-target.Transform.position).sqrMagnitude;
                for(var i=1;i<humans.Length;i++)
                {
                    var candidateDistance=(pos-humans[i].Transform.position).sqrMagnitude;
                    if(candidateDistance>=distanceSquared)continue;
                    target=humans[i];distanceSquared=candidateDistance;
                }
                var distance=Mathf.Sqrt(distanceSquared);var owner=z.AIData?.BotOwner;if(owner?.Memory==null)continue;
                if(distance<=25){_near25++;if(Mathf.Abs(pos.y-target.Transform.position.y)<=3)_sameFloorNear25++;}
                if(distance<=50)_near50++;if(distance<=100)_near100++;
                if(WaitForInfectedActivation(z,owner)||PauseIdleActor(z,owner,humans,distance))continue;
                var memory=owner.Memory;
                var contact=UpdateVisualGoal(z,owner,target,distance);
                if(memory.GoalEnemy!=null){_nativeEnemies++;if(distance>Plugin.LoudMax*(GroundZero?Plugin.Hearing.GroundZeroScale:1))_distantEnemies++;}
                if(_attention.TryGetValue(z,out var currentAttention)&&currentAttention.Until>Time.time)
                {
                    _investigating++;
                    if(distance<=25&&Mathf.Abs(pos.y-target.Transform.position.y)<=3 &&
                       (currentAttention.PlayerGunfire || currentAttention.Reason is "sighting" or "local_sighting" or "explosion"))
                    {
                        _localResponders++;
                        if(memory.GoalEnemy?.IsVisible==true)_localChasers++;
                        _peakLocalResponders=Mathf.Max(_peakLocalResponders,_localResponders);
                    }
                }
                RefreshInvestigationRoute(z,owner,contact||memory?.GoalEnemy!=null);
                if(!_open.TryGetValue(z,out var nextDoor)||Time.time>=nextDoor)
                {
                    _open[z]=Time.time+1.2f;
                    foreach(var door in _doors)
                    {
                        if(door==null||door.DoorState!=EDoorState.Shut||(door.transform.position-pos).sqrMagnitude>5.0f)continue;
                        // Use the game's door interaction. Locked doors are never unlocked or breached.
                        door.Interact(EInteractionType.Open);
                        _doorOpens++;
                    }
                }
                var melee=Get(Get(owner,"WeaponManager"),"Melee");var knife=Get(melee,"KnifeController");
                if(knife==null)continue; // Armed infected investigate copied positions and retain visible-target shooting.
                if(contact&&(!_swing.TryGetValue(z,out var swingTime)||Time.time>=swingTime))
                {
                    _swing[z]=Time.time+1.15f;var steering=Get(owner,"Steering");if(steering!=null)VectorCall(steering,"LookToPoint",target.Transform.position+Vector3.up*1.3f);
                    var method=knife.GetType().GetMethod("MakeKnifeKick",Flags,null,Type.EmptyTypes,null)??throw new MissingMethodException("MakeKnifeKick");method.Invoke(knife,null);
                    _swings++;
                }
            }
            catch(Exception e){if(!_reportedError){Plugin.Log.LogError("[SPT-Z] Native zombie action failed: "+e);_reportedError=true;}}
        }
    }
    private void OnDestroy()
    {
        if(Current==this){Current=null;ThreatActors.Reset();InfectedTurning.Reset();}
        if(_world!=null)
        {
            CombatSkillPractice.End(_world);
            ClientDiagnostics.EndRaid(_world);
        }
        if(_world!=null)_world.OnPersonAdd-=Added;
        if(_events!=null){_events.OnSoundPlayed-=Sound;_events.OnGrenadeExplosive-=Explosion;}
    }
}
