using System;
using System.Collections.Generic;
namespace Codex.ZombieSurvival.Rules;

public readonly struct RoamPoint
{
    public readonly float X,Y,Z;
    public RoamPoint(float x,float y,float z){X=x;Y=y;Z=z;}
    public bool Finite=>!float.IsNaN(X)&&!float.IsNaN(Y)&&!float.IsNaN(Z)&&!float.IsInfinity(X)&&!float.IsInfinity(Y)&&!float.IsInfinity(Z);
    public float FlatDistanceSquared(RoamPoint other){var x=X-other.X;var z=Z-other.Z;return x*x+z*z;}
}
public sealed class PatrolSettings
{
    public float Radius=24,Spacing=6,HoldMin=10,HoldMax=24;
}
public static class PatrolPolicy
{
    public static float Setting(float value,float minimum,float maximum,float fallback)=>float.IsNaN(value)||float.IsInfinity(value)?fallback:Math.Max(minimum,Math.Min(maximum,value));
    public static uint Seed(string id)
    {
        uint hash=2166136261;foreach(var c in id){hash^=c;hash=unchecked(hash*16777619);}return hash;
    }
    private static float Unit(uint seed,int cycle,int sample)
    {
        var n=unchecked(seed+(uint)cycle*747796405u+(uint)sample*2891336453u);
        n=((n>>16)^n)*2246822519u;n=((n>>13)^n)*3266489917u;n=(n>>16)^n;
        return (n&0xffffff)/16777216f;
    }
    public static float Hold(PatrolSettings settings,uint seed,int cycle)=>settings.HoldMin+(settings.HoldMax-settings.HoldMin)*Unit(seed,cycle,97);
    public static RoamPoint Choose(PatrolSettings settings,uint seed,int cycle,RoamPoint home,RoamPoint current,IReadOnlyList<RoamPoint> neighbours)
    {
        var best=home;var bestScore=float.NegativeInfinity;var spacing2=settings.Spacing*settings.Spacing;
        for(var candidate=0;candidate<12;candidate++)
        {
            var angle=Unit(seed,cycle,candidate*2)*(float)(Math.PI*2);
            var radius=settings.Radius*(.25f+.75f*(float)Math.Sqrt(Unit(seed,cycle,candidate*2+1)));
            var point=new RoamPoint(home.X+(float)Math.Cos(angle)*radius,home.Y,home.Z+(float)Math.Sin(angle)*radius);
            var travel=(float)Math.Sqrt(point.FlatDistanceSquared(current));
            var score=Unit(seed,cycle,candidate+101)*.25f-Math.Abs(travel-12)*.035f;
            foreach(var other in neighbours)
            {
                if(!other.Finite||Math.Abs(other.Y-point.Y)>3)continue;
                var d=point.FlatDistanceSquared(other);
                if(d<spacing2)score-=8*(1-d/spacing2);
            }
            if(score>bestScore){bestScore=score;best=point;}
        }
        return best;
    }
}
public sealed class PatrolPlan
{
    private readonly uint _seed;
    private int _cycle;
    private RoamPoint _revisionCentre;
    public RoamPoint Home{get;private set;}
    public RoamPoint Centre{get;private set;}
    public bool FollowingInformation{get;private set;}
    public float HoldSeconds{get;private set;}
    public float NextIdlePlan{get;private set;}
    public int Revision{get;private set;}
    public PatrolPlan(uint seed,RoamPoint home){_seed=seed;Home=Centre=home;}
    public bool NeedsIdlePlan(float now,bool informed)=>!informed&&(FollowingInformation||now>=NextIdlePlan);
    public bool Update(PatrolSettings settings,float now,RoamPoint current,RoamPoint? information,float until,IReadOnlyList<RoamPoint> neighbours)
    {
        var informed=information.HasValue&&information.Value.Finite&&AwarenessPolicy.InformationAlive(now,until);
        if(informed)
        {
            var changed=!FollowingInformation||_revisionCentre.FlatDistanceSquared(information!.Value)>16||Math.Abs(_revisionCentre.Y-information.Value.Y)>3;
            FollowingInformation=true;Centre=information!.Value;HoldSeconds=3;
            if(changed){Revision++;_revisionCentre=Centre;}
            return changed;
        }
        if(!NeedsIdlePlan(now,false))return false;
        // After investigating, settle around the actual current location rather than following a stale cue.
        if(FollowingInformation){Home=current;FollowingInformation=false;}
        Centre=PatrolPolicy.Choose(settings,_seed,++_cycle,Home,current,neighbours);
        HoldSeconds=PatrolPolicy.Hold(settings,_seed,_cycle);NextIdlePlan=now+HoldSeconds;Revision++;_revisionCentre=Centre;
        return true;
    }
}
