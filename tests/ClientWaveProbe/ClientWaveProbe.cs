using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace Ember.Tests;

// Explicitly installed in Development only. It is not a release package project.
[BepInPlugin("codex.ember.wave-qa","Ember Development Wave Probe","1.0.0")]
public sealed class ClientWaveProbe:BaseUnityPlugin
{
    private string _root="",_lastCommand="";
    private static ClientWaveProbe? Instance;
    private static bool Synthetic;
    private static readonly FieldInfo PlayerBody=AccessTools.Field(typeof(Player),"_playerBody"), PlateColliders=AccessTools.Field(typeof(Player),"_armorPlateColliders");
    private sealed class Impact { public Player Target=null!; public EBodyPart Part; public float Before,ArmorBefore; public DamageInfoStruct Damage; }
    private bool _allowed;
    private float _next,_seconds;private int _frames;
    private MethodInfo? _snapshot;
    private void Awake()
    {
        _root=Path.GetFullPath(Path.Combine(BepInEx.Paths.PluginPath,"..",".."));
        try
        {
            var state=JObject.Parse(File.ReadAllText(Path.Combine(_root,".sptz","installed.json")));
            var marker=JObject.Parse(File.ReadAllText(Path.Combine(_root,".sptz","ui-qa.json")));
            _allowed=(string?)state["instanceKind"]=="Development"&&(int?)state["port"]==6974&&((string?)marker["username"])?.StartsWith("Ember-UI-QA-",StringComparison.Ordinal)==true;
            if(!_allowed)Logger.LogError("QA probe refused a non-Development installation.");
            else { Instance=this;new Harmony("codex.ember.threat-qa").Patch(AccessTools.Method(typeof(Player),"ApplyShot"),prefix:new HarmonyMethod(typeof(ClientWaveProbe),nameof(BeforeShot)),postfix:new HarmonyMethod(typeof(ClientWaveProbe),nameof(AfterShot))); }
        }catch{_allowed=false;}
    }

    private static float ArmorDurability(Player p)
    {
        var armor=new List<ArmorComponent>();p.Inventory.GetPutOnArmorsNonAlloc(armor);return armor.Sum(a=>a.Repairable.Durability);
    }
    private static void BeforeShot(Player __instance,DamageInfoStruct __0,EBodyPart __1,out Impact? __state)
    {
        __state=null;
        if(Instance?._allowed!=true||!Singleton<GameWorld>.Instantiated||Singleton<GameWorld>.Instance.MainPlayer?.Profile.Info.Nickname!="EmberUITest"||!__instance.IsAI||__0.DamageType!=EDamageType.Bullet)return;
        __state=new Impact{Target=__instance,Part=__1,Before=__instance.HealthController.GetBodyPartHealth(__1).Current,ArmorBefore=ArmorDurability(__instance),Damage=__0};
    }
    private static void AfterShot(Impact? __state)
    {
        if(__state is null||Instance is null)return;
        var s=__state;var id=s.Target.Profile.Id.ToString();
        var row=new{utc=DateTimeOffset.UtcNow,kind=Synthetic?"synthetic_native_ballistic_contract":"real_projectile_impact",unit=id.StartsWith("7a32")?id.Substring(4,4):"native",bodyPart=s.Part.ToString(),penetration=s.Damage.PenetrationPower,rawDamage=s.Damage.Damage,blocked=s.Damage.BlockedBy.HasValue,deflected=s.Damage.DeflectedBy.HasValue,hpBefore=s.Before,hpAfter=s.Target.HealthController.GetBodyPartHealth(s.Part).Current,armorBefore=s.ArmorBefore,armorAfter=ArmorDurability(s.Target),alive=s.Target.HealthController.IsAlive,playerInstanceUsed=false};
        File.AppendAllText(Path.Combine(Instance._root,".sptz","armor-impacts.jsonl"),JsonConvert.SerializeObject(row)+Environment.NewLine);
    }
    private static object Equipment(Player player)
    {
        var body=PlayerBody.GetValue(player) as EFT.PlayerBody;var armor=new List<ArmorComponent>();player.Inventory.GetPutOnArmorsNonAlloc(armor);
        var id=player.Profile.Id.ToString();
        return new{unit=id.StartsWith("7a32")?id.Substring(4,4):"native",head=player.HealthController.GetBodyPartHealth(EBodyPart.Head).Current,position=new{player.Transform.position.x,player.Transform.position.y,player.Transform.position.z},velocity=player.Velocity.magnitude,plateColliders=(PlateColliders.GetValue(player) as Array)?.Length??0,hitRegions=player.GetComponentsInChildren<BodyPartCollider>().Select(c=>c.BodyPartColliderType.ToString()).Distinct(),
            armor=armor.Select(a=>new{tpl=a.Item.StringTemplateId,level=a.ArmorClass,durability=a.Repairable.Durability}),
            visibleSlots=new[]{EquipmentSlot.ArmorVest,EquipmentSlot.Headwear}.Select(slot=>new{slot=slot.ToString(),item=player.Inventory.Equipment.GetSlot(slot).ContainedItem?.StringTemplateId,renderers=body?.SlotViews.GetByKey(slot)?.Renderers?.Count(r=>r!=null&&r.enabled&&r.gameObject.activeInHierarchy)??0})};
    }


    private sealed class FixedRandom:GInterface470
    {
        public Quaternion GetRandomRotation(int seed)=>Quaternion.identity;
        public float GetRandomFloat(int seed)=>.5f;
        public Vector3 GetRandomDirection(int seed)=>Vector3.forward;
        public float GetNormalDistributedRandomFloat(int seed,float mean,float deviation)=>mean;
    }
    private static EftBulletClass TestShot(float penetration,Vector3 direction)
    {
        var shot=(EftBulletClass)Activator.CreateInstance(typeof(EftBulletClass),true)!;
        shot.PenetrationPower=penetration;shot.Direction=direction;shot.Damage=50;shot.ArmorDamage=.3f;shot.Randoms=new FixedRandom();shot.RandomSeed=1;return shot;
    }
    private void ArmorContract(GameWorld world,Player[] alive)
    {
        var heavy=alive.Where(p=>{var armor=new List<ArmorComponent>();p.Inventory.GetPutOnArmorsNonAlloc(armor);return armor.Any(a=>a.ArmorClass==5);}).Take(2).ToArray();
        if(heavy.Length<2)throw new InvalidOperationException("Need two native class-five infected for the contract");
        var results=new List<object>();Synthetic=true;
        try
        {
            for(var i=0;i<2;i++)
            {
                var p=heavy[i];var body=p.GetComponentsInChildren<BodyPartCollider>().First(c=>c.BodyPartType==EBodyPart.Chest);
                var hit=body.GetComponent<Collider>();var direction=-p.Transform.forward;var point=hit.bounds.center+p.Transform.forward*.1f;
                var shot=TestShot(i==0?5:54,direction);p.SetShotStatus(body,shot,point,p.Transform.forward,direction);
                var before=p.HealthController.GetBodyPartHealth(EBodyPart.Chest).Current;var armorBefore=ArmorDurability(p);
                var info=new DamageInfoStruct{DamageType=EDamageType.Bullet,Damage=50,ArmorDamage=.3f,PenetrationPower=shot.PenetrationPower,Direction=direction,HitPoint=point,HitNormal=p.Transform.forward,HitCollider=hit,HittedBallisticCollider=body,IsForwardHit=true,BlockedBy=shot.BlockedBy,DeflectedBy=shot.DeflectedBy,SourceId="573719762459775a626ccbc1",Player=world.GetEverExistedBridgeByProfileID(world.MainPlayer.ProfileId)};
                p.ApplyShot(info,EBodyPart.Chest,body.BodyPartColliderType,(EArmorPlateCollider)0,ShotIdStruct.EMPTY_SHOT_ID);
                results.Add(new{penetration=shot.PenetrationPower,blocked=shot.BlockedBy.HasValue,hpBefore=before,hpAfter=p.HealthController.GetBodyPartHealth(EBodyPart.Chest).Current,armorBefore,armorAfter=ArmorDurability(p)});
            }
            var head=heavy[0].GetComponentsInChildren<BodyPartCollider>().First(c=>c.BodyPartType==EBodyPart.Head);var hc=head.GetComponent<Collider>();
            var front=-heavy[0].Transform.forward;var top=TestShot(5,front);var face=TestShot(5,front);
            heavy[0].SetShotStatus(head,top,hc.bounds.center+Vector3.up*hc.bounds.extents.y*.8f,-front,front);
            var faceCollider=heavy[0].GetComponentsInChildren<BodyPartCollider>().FirstOrDefault(c=>c.BodyPartColliderType==EBodyPartColliderType.Eyes)??head;
            heavy[0].SetShotStatus(faceCollider,face,hc.bounds.center,-front,front);
            var report=new{utc=DateTimeOffset.UtcNow,kind="synthetic_native_ballistic_contract",results,helmetTopBlocks=top.BlockedBy.HasValue,openFaceUnblocked=!face.BlockedBy.HasValue,realProjectileTrajectory=false,playerInstanceUsed=false};
            File.WriteAllText(Path.Combine(_root,".sptz","armor-contract.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
        }
        finally{Synthetic=false;}
    }

    private void Update()
    {
        if(!_allowed)return;
        _frames++;_seconds+=Time.unscaledDeltaTime;
        if(Time.unscaledTime<_next)return;_next=Time.unscaledTime+1;
        try
        {
            if(!Singleton<GameWorld>.Instantiated)return;var world=Singleton<GameWorld>.Instance;var main=world.MainPlayer;
            if(main is null||main.Profile.Info.Nickname!="EmberUITest")return;
            // Dedicated QA actor only: keep it alive while testing native NPC batches.
            if(main.HealthController is ActiveHealthController qaHealth)qaHealth.SetDamageCoeff(0);
            if(_snapshot is null)_snapshot=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Codex.ZombieSurvival.Client.FiniteWaves",false)).FirstOrDefault(t=>t is not null)?.GetMethod("Diagnostic",BindingFlags.Static|BindingFlags.NonPublic);
            if(_snapshot is null)return;
            var budget=JObject.FromObject(_snapshot.Invoke(null,null)!);
            var raidType=_snapshot.DeclaringType!.Assembly.GetType("Codex.ZombieSurvival.Client.SurvivalRaid",true);
            var raidReady=world.GetComponent(raidType) is not null;
            var alive=world.AllAlivePlayersList.OfType<Player>().Where(p=>p.IsAI&&!p.IsYourPlayer&&p.HealthController?.IsAlive==true&&p.Profile.Info.Settings.Role.ToString().StartsWith("infected",StringComparison.OrdinalIgnoreCase)).ToArray();
            var commandPath=Path.Combine(_root,".sptz","wave-probe-command.json");
            if(File.Exists(commandPath))
            {
                var command=JObject.Parse(File.ReadAllText(commandPath));var id=(string?)command["id"]??"";
                if(id.Length>0&&id!=_lastCommand&&command["expiresUtc"]?.ToObject<DateTimeOffset>()>DateTimeOffset.UtcNow)
                {
                    _lastCommand=id;
                    if((string?)command["action"]=="armor_contract")ArmorContract(world,alive);
                    var keep=(string?)command["action"]=="kill_to_two"?2:(string?)command["action"]=="kill_all"?0:alive.Length;
                    foreach(var bot in alive.Take(Math.Max(0,alive.Length-keep)))if(bot.HealthController is ActiveHealthController health)health.Kill((EDamageType)0);
                }
            }
            var report=new{utc=DateTimeOffset.UtcNow,map=main.Location,playerAlive=main.HealthController.IsAlive,profile="dedicated-qa",raidReady,budget,nativeAlive=alive.Length,fps=_seconds>0?_frames/_seconds:0,render=new{Screen.width,Screen.height},lastCommand=_lastCommand,mainPosition=new{main.Transform.position.x,main.Transform.position.y,main.Transform.position.z},actors=alive.Select(Equipment),syntheticNpcDeaths=true,qaPlayerInvulnerable=true,realPlayerUsed=false};
            var path=Path.Combine(_root,".sptz","wave-probe-state.json");var temporary=path+".tmp";File.WriteAllText(temporary,JsonConvert.SerializeObject(report));
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            _frames=0;_seconds=0;
        }catch(Exception e){Logger.LogWarning("Wave QA: "+e.Message);}
    }
}
