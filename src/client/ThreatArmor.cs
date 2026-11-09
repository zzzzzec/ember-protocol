using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Codex.ZombieSurvival.Client;

// Infected use a simplified skeleton without the ordinary plate volumes. Map its
// existing hit surface into native plate/helmet regions; keep native resistance,
// ricochet, durability, blunt damage and health application. No new physics objects.
internal static class ThreatArmor
{
    private struct Context { public bool Active, Head; public EFT.InventoryLogic.Inventory? Inventory; public int Plates; public EBodyPartColliderType HeadPart; }
    [ThreadStatic] private static Context _current;
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(Player),"SetShotStatus"),prefix:new HarmonyMethod(typeof(ThreatArmor),nameof(BeginStatus)),finalizer:new HarmonyMethod(typeof(ThreatArmor),nameof(Restore)));
        h.Patch(AccessTools.Method(typeof(Player),"ApplyShot"),prefix:new HarmonyMethod(typeof(ThreatArmor),nameof(BeginDamage)),finalizer:new HarmonyMethod(typeof(ThreatArmor),nameof(Restore)));
        h.Patch(AccessTools.Method(typeof(EFT.InventoryLogic.Inventory),"GetPutOnArmorsNonAlloc"),postfix:new HarmonyMethod(typeof(ThreatArmor),nameof(OrderArmor)));
        h.Patch(AccessTools.Method(typeof(ArmorComponent),"ShotMatches"),prefix:new HarmonyMethod(typeof(ThreatArmor),nameof(Match)));
    }
    private static Context Resolve(Player player,EBodyPart part,EBodyPartColliderType collider,Vector3 direction,Vector3 point,Collider? hit)
    {
        if(!ThreatActors.SimplifiedArmor(player))return default;
        var incoming=-direction.normalized;
        var forward=Vector3.Dot(incoming,player.Transform.forward);var right=Vector3.Dot(incoming,player.Transform.right);
        if(part==EBodyPart.Chest && collider is EBodyPartColliderType.RibcageLow or EBodyPartColliderType.RibcageUp
            or EBodyPartColliderType.SpineTop or EBodyPartColliderType.SpineDown
            or EBodyPartColliderType.LeftSideChestUp or EBodyPartColliderType.LeftSideChestDown
            or EBodyPartColliderType.RightSideChestUp or EBodyPartColliderType.RightSideChestDown)
        {
            // Native plate templates use both SAPI and Russian plate shape masks.
            // Head, pelvis, throat and arms retain their own original coverage.
            var mask=forward>.5f?1|64:forward<-.5f?2|128:right<0?4|8|256|512:16|32|1024|2048;
            return new Context{Active=true,Inventory=player.Inventory,Plates=mask};
        }
        if(part==EBodyPart.Head&&collider==EBodyPartColliderType.HeadCommon)
        {
            var centre=hit!=null?hit.bounds.center.y:player.Transform.position.y+1.6f;
            // Open-face helmets leave the eyes and jaw exposed. Never turn a helmet
            // into uniform whole-head armor on the simplified zombie head capsule.
            var mapped=point.y>centre+.035f?EBodyPartColliderType.ParietalHead:
                forward<-.35f?EBodyPartColliderType.BackHead:Mathf.Abs(right)>.65f?EBodyPartColliderType.Ears:
                point.y<centre-.055f?EBodyPartColliderType.Jaw:EBodyPartColliderType.Eyes;
            return new Context{Active=true,Inventory=player.Inventory,Head=true,HeadPart=mapped};
        }
        return default;
    }
    private static void BeginStatus(Player __instance,BodyPartCollider __0,Vector3 __2,Vector3 __4,out Context __state)
    {__state=_current;_current=Resolve(__instance,__0.BodyPartType,__0.BodyPartColliderType,__4,__2,__0.GetComponent<Collider>());}
    private static void BeginDamage(Player __instance,DamageInfoStruct __0,EBodyPart __1,EBodyPartColliderType __2,out Context __state)
    {__state=_current;_current=Resolve(__instance,__1,__2,__0.Direction,__0.HitPoint,__0.HitCollider);}
    private static Exception? Restore(Exception? __exception,Context __state){_current=__state;return __exception;}
    private static void OrderArmor(EFT.InventoryLogic.Inventory __instance,List<ArmorComponent> __0)
    {
        if(!_current.Active||_current.Head||!ReferenceEquals(_current.Inventory,__instance))return;
        // Physical plate hits normally precede the soft liner. Preserve that order
        // when a simplified chest hit has to stand in for the missing plate volumes.
        var front=0;
        for(var i=0;i<__0.Count;i++)
            if(((int)__0[i].ArmorPlateCollidersMask&_current.Plates)!=0)
            {var plate=__0[i];__0.RemoveAt(i);__0.Insert(front++,plate);}
    }
    private static void Match(ArmorComponent __instance,ref EBodyPartColliderType __0,ref EArmorPlateCollider __1)
    {
        if(!_current.Active)return;
        if(_current.Head){__0=_current.HeadPart;return;}
        if(__1!=(EArmorPlateCollider)0)return;
        var intersection=(int)__instance.ArmorPlateCollidersMask&_current.Plates;
        if(intersection!=0)__1=(EArmorPlateCollider)(intersection&-intersection);
    }
}
