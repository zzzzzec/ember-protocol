using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;
using Codex.ZombieSurvival.Rules;
namespace Codex.ZombieSurvival.Client;

internal static class InfectedPatrol
{
    private static readonly FieldInfo EventOwner=AccessTools.Field(typeof(BotHalloweenWithZombiesGameEvent),"BotOwner_0");
    private static readonly FieldInfo LayerOwner=AccessTools.Field(typeof(GClass99),"BotOwner_0");
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(BotHalloweenWithZombiesGameEvent),"FindPointCenterPosition"),prefix:new HarmonyMethod(typeof(InfectedPatrol),nameof(Centre)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(BotHalloweenWithZombiesGameEvent),"HoldPeriod"),prefix:new HarmonyMethod(typeof(InfectedPatrol),nameof(Hold)));
        harmony.Patch(AccessTools.Method(typeof(BotHalloweenWithZombiesGameEvent),"TryRecalcPatrolParams"),prefix:new HarmonyMethod(typeof(InfectedPatrol),nameof(Recalculate)));
        harmony.Patch(AccessTools.Method(typeof(GClass99),"FindPoint"),prefix:new HarmonyMethod(typeof(InfectedPatrol),nameof(FindPoint)));
    }
    private static bool Plan(object instance,FieldInfo field,out BotOwner owner,out PatrolPlan plan)
    {
        owner=(field.GetValue(instance) as BotOwner)!;plan=null!;
        return owner!=null&&Plugin.Infected(owner.GetPlayer)&&SurvivalRaid.Current?.TryPatrol(owner.GetPlayer,out plan)==true;
    }
    private static bool Centre(BotHalloweenWithZombiesGameEvent __instance,ref Vector3 __result)
    {
        if(!Plan(__instance,EventOwner,out _,out var plan))return true;
        __result=new Vector3(plan.Centre.X,plan.Centre.Y,plan.Centre.Z);return false;
    }
    private static bool Hold(BotHalloweenWithZombiesGameEvent __instance,ref float __result)
    {
        if(!Plan(__instance,EventOwner,out _,out var plan))return true;
        __result=plan.HoldSeconds;return false;
    }
    private static bool Recalculate(BotHalloweenWithZombiesGameEvent __instance)
    {
        if(!Plan(__instance,EventOwner,out var owner,out var plan))return true;
        __instance.CachedLeader=owner;
        __instance.FindPointCenterPosition_1=new Vector3(plan.Centre.X,plan.Centre.Y,plan.Centre.Z);
        __instance.HoldPeriod_1=plan.HoldSeconds;__instance.NextChangePositionTime=plan.NextIdlePlan;
        return false;
    }
    private static void FindPoint(GClass99 __instance)
    {
        if(!Plan(__instance,LayerOwner,out var owner,out var plan))return;
        if(SurvivalRaid.Current!.RoamPointChanged(owner.GetPlayer,plan.Revision))__instance.CustomNavigationPoint_0=null;
        // Native code still chooses an available navigable cover point; no per-frame path search is added.
    }
}
public sealed partial class SurvivalRaid
{
    private readonly Dictionary<Player,PatrolPlan> _patrols=new();
    private readonly Dictionary<Player,int> _patrolCoverRevisions=new();
    private readonly Dictionary<Player,int> _investigationRevisions=new();
    private int _idlePatrolPlans,_patrolInformationChanges;
    private float _nextPatrolSample;
    private static RoamPoint PatrolPosition(Vector3 p)=>new(p.x,p.y,p.z);
    internal bool TryPatrol(Player player,out PatrolPlan plan)
    {
        if(player==null||player.HealthController?.IsAlive!=true){plan=null!;return false;}
        if(!_patrols.TryGetValue(player,out plan!))
        {
            if(player.HealthController?.IsAlive!=true){plan=null!;return false;}
            plan=new PatrolPlan(PatrolPolicy.Seed(player.Profile.Id),PatrolPosition(player.Transform.position));_patrols[player]=plan;
        }
        var now=Time.time;var current=PatrolPosition(player.Transform.position);
        var informed=_attention.TryGetValue(player,out var attention)&&AwarenessPolicy.InformationAlive(now,attention.Until);
        IReadOnlyList<RoamPoint> neighbours=Array.Empty<RoamPoint>();
        if(plan.NeedsIdlePlan(now,informed))
        {
            var close=new List<RoamPoint>(32);var range2=Plugin.Patrol.Radius*Plugin.Patrol.Radius*4;
            foreach(var other in _infected)
            {
                if(other==null||other==player||other.HealthController?.IsAlive!=true)continue;
                var p=PatrolPosition(other.Transform.position);
                if(Math.Abs(p.Y-current.Y)>3||p.FlatDistanceSquared(current)>range2)continue;
                // Alerted actors may converge freely. Idle actors softly avoid existing idle positions and plans.
                if(_attention.TryGetValue(other,out var signal)&&signal.Until>now)continue;
                close.Add(p);
                if(close.Count<32&&_patrols.TryGetValue(other,out var neighbour)&&!neighbour.FollowingInformation)close.Add(neighbour.Centre);
                if(close.Count>=32)break;
            }
            neighbours=close;
        }
        var previousMode=plan.FollowingInformation;
        if(plan.Update(Plugin.Patrol,now,current,informed?PatrolPosition(attention!.Point):(RoamPoint?)null,informed?attention!.Until:0,
            neighbours,informed?AwarenessPolicy.CuePriority(attention!.Reason):0))
        {
            if(plan.FollowingInformation)_patrolInformationChanges++;else _idlePatrolPlans++;
            if(!plan.FollowingInformation||previousMode!=plan.FollowingInformation)
                ClientDiagnostics.Record("patrol.plan",new{id=ClientDiagnostics.Key(player.Profile.Id),mode=plan.FollowingInformation?"investigate":"wander",reason=informed?attention!.Reason:"none",centre=new{x=plan.Centre.X,y=plan.Centre.Y,z=plan.Centre.Z},hold=plan.HoldSeconds});
        }
        return true;
    }
    internal bool RoamPointChanged(Player player,int revision)
    {
        if(_patrolCoverRevisions.TryGetValue(player,out var old)&&old==revision)return false;
        _patrolCoverRevisions[player]=revision;return true;
    }
    private void RefreshInvestigationRoute(Player player,BotOwner owner,bool fighting)
    {
        if(fighting){_investigationRevisions.Remove(player);return;}
        var hadRoute=_investigationRevisions.TryGetValue(player,out var previousRevision);
        var informed=_attention.TryGetValue(player,out var cue)&&AwarenessPolicy.InformationAlive(Time.time,cue.Until);
        if(!informed&&!hadRoute)return;
        if(!TryPatrol(player,out var plan))return;
        if(plan.FollowingInformation)
        {
            if(hadRoute&&previousRevision==plan.Revision)return;
            _investigationRevisions[player]=plan.Revision;
        }
        else
        {
            if(!_investigationRevisions.Remove(player))return;
        }
        // Restart the native patrol decision once per change of destination.
        // Its cover action owns both the route and the movement.
        owner.Memory.SetCoverPoints(null,"Ember investigate");
        owner.Memory.CheckIsInCover2();
        owner.Brain?.BaseBrain?.CalcActionNextFrame();
        ClientDiagnostics.Record("patrol.route",new{id=ClientDiagnostics.Key(player.Profile.Id),
            mode=plan.FollowingInformation?"investigate":"wander",reason=informed?cue!.Reason:"expired",
            revision=plan.Revision,centre=new{x=plan.Centre.X,y=plan.Centre.Y,z=plan.Centre.Z}});
    }
    private void ForgetPatrol(Player player){_patrols.Remove(player);_patrolCoverRevisions.Remove(player);_investigationRevisions.Remove(player);}
    private void SamplePatrol()
    {
        if(Time.time<_nextPatrolSample)return;_nextPatrolSample=Time.time+15;
        var actors=new List<object>(_infected.Count);
        foreach(var player in _infected)
        {
            if(player==null||player.HealthController?.IsAlive!=true)continue;
            _patrols.TryGetValue(player,out var plan);var pos=player.Transform.position;
            var informed=_attention.TryGetValue(player,out var cue)&&cue.Until>Time.time;
            var owner=player.AIData?.BotOwner;
            var destination=owner?.Memory?.CurCustomCoverPoint?.Position;
            actors.Add(new{id=ClientDiagnostics.Key(player.Profile.Id),position=new{pos.x,pos.y,pos.z},mode=informed?"investigate":"wander",reason=informed?cue!.Reason:"none",centre=plan==null?null:new{x=plan.Centre.X,y=plan.Centre.Y,z=plan.Centre.Z},remaining=informed?cue!.Until-Time.time:Math.Max(0,(plan?.NextIdlePlan??0)-Time.time),
                nativeDestination=destination is { } point?new{point.x,point.y,point.z}:null,nativePath=owner?.Mover?.HasPathAndNoComplete==true});
        }
        ClientDiagnostics.Record("patrol.sample",new{idlePlans=_idlePatrolPlans,informationChanges=_patrolInformationChanges,actors});
    }
}
