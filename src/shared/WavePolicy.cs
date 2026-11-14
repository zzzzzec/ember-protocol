using System;
using System.Collections.Generic;
namespace Codex.ZombieSurvival.Rules;

// Same accounting code runs in the client and in the headless regression harness.
public sealed class WaveBudget
{
    private readonly List<int> _order=new();
    private readonly HashSet<int> _known=new(),_issued=new(),_spawned=new(),_opening=new(),_activating=new();
    private readonly Dictionary<string,int> _profiles=new();
    private readonly Dictionary<int,string> _ticketProfiles=new();
    private readonly HashSet<string> _alive=new(),_dead=new();
    private int _cursor,_batchRemaining;
    private double _readyAt=double.NaN,_nextIssue,_nextActivation;
    public int Cap { get; }
    public int Threshold { get; }
    public double Pause { get; }
    public double Spacing { get; }
    public bool Steady { get; }
    public int PendingLimit { get; }
    public int GroupSize { get; }
    public double Jitter { get; }
    public uint Seed { get; }
    public int Activating=>_activating.Count;
    public double NextActivationAt=>_nextActivation;
    public bool Sealed { get; private set; }
    public bool Ended { get; private set; }
    public int Batch { get; private set; }=1;
    public int Total=>_known.Count;
    public int Issued=>_issued.Count;
    public int Spawned=>_spawned.Count;
    public int Alive=>_alive.Count;
    public int Pending=>Issued-Spawned;
    public int Waiting=>Total-Issued;
    public bool Cleared=>Sealed&&Spawned==Total&&Pending==0&&Alive==0;
    public WaveBudget(int cap,int threshold,double pause,double spacing,bool steady=false,int pendingLimit=2,double jitter=.25,uint seed=0,int groupSize=0)
    {
        Cap=Math.Max(1,Math.Min(64,cap));Threshold=Math.Max(0,Math.Min(Cap-1,threshold));
        Pause=Finite(pause)?Math.Max(0,pause):8;Spacing=Finite(spacing)?Math.Max(.1,spacing):.35;
        Steady=steady;PendingLimit=Math.Max(1,Math.Min(Cap,pendingLimit));Jitter=Finite(jitter)?Math.Max(0,Math.Min(.5,jitter)):.25;Seed=seed;
        GroupSize=Math.Max(0,Math.Min(Cap,groupSize));
    }
    private static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    public bool Register(int ticket)
    {
        if(Ended||Sealed||!_known.Add(ticket))return false;
        _order.Add(ticket);return true;
    }
    public bool Opening(int ticket)
    {
        if(Ended||Sealed||!_known.Contains(ticket)||Issued>=Cap||!_issued.Add(ticket))return false;
        _opening.Add(ticket);
        return true;
    }
    public void Seal(){Sealed=true;_cursor=0;}
    public int? TakeNext(double now, Func<int,int,double>? priority = null, Func<int,bool>? eligible = null)
    {
        if(Ended||!Sealed||!Finite(now)||Waiting==0)return null;
        if(Steady)return TakeSteady(now,priority,eligible);
        if(_batchRemaining==0)
        {
            if(Pending!=0||Alive>Threshold){_readyAt=double.NaN;return null;}
            if(double.IsNaN(_readyAt)){_readyAt=now+Pause;return null;}
            if(now<_readyAt)return null;
            Batch++;_batchRemaining=Math.Min(Cap-Alive,Waiting);_readyAt=double.NaN;
        }
        if(now<_nextIssue||Alive+Pending>=Cap)return null;
        while(_cursor<_order.Count&&_issued.Contains(_order[_cursor]))_cursor++;
        if(_cursor>=_order.Count)return null;
        var id=_order[_cursor++];_issued.Add(id);_batchRemaining--;_nextIssue=now+Spacing;return id;
    }
    private int? TakeSteady(double now,Func<int,int,double>? priority,Func<int,bool>? eligible)
    {
        // Reserve only a small number of native requests. Unsafe positions can wait
        // without filling every free slot with profiles that later activate together.
        var waitingForGroup=GroupSize>0&&_batchRemaining==0;
        if(Alive+Pending>=Cap||Pending>=PendingLimit||waitingForGroup&&(Alive>Threshold||Pending>0))
        {_readyAt=double.NaN;return null;}
        if(GroupSize==0||waitingForGroup)
        {
            if(double.IsNaN(_readyAt)){_readyAt=now+Pause;return null;}
            if(now<_readyAt)return null;
        }
        if(now<_nextIssue)return null;
        while(_cursor<_order.Count&&_issued.Contains(_order[_cursor]))_cursor++;
        var firstIndex = _cursor;
        while (firstIndex < _order.Count && (_issued.Contains(_order[firstIndex]) || eligible?.Invoke(_order[firstIndex]) == false))
            firstIndex++;
        if (firstIndex == _order.Count) return null;
        var first = _order[firstIndex];
        var ticket = first;
        if (priority is not null)
        {
            var best = priority(first, first);
            if (!Finite(best)) best = double.PositiveInfinity;
            for (var i = firstIndex + 1; i < _order.Count; i++)
            {
                var candidate = _order[i];
                if (_issued.Contains(candidate) || eligible?.Invoke(candidate) == false) continue;
                var score = priority(first, candidate);
                if (score >= best || !Finite(score)) continue;
                ticket = candidate;
                best = score;
            }
        }
        if(waitingForGroup)
        {
            Batch++;
            _batchRemaining=Math.Min(GroupSize,Math.Min(Cap-Alive,Waiting));
        }
        _issued.Add(ticket);
        if(GroupSize>0)_batchRemaining--;else Batch=2;
        _readyAt=double.NaN;_nextIssue=now+Interval(ticket);return ticket;
    }
    public bool ReturnUnbound(int ticket)
    {
        if (!Steady || Ended || _opening.Contains(ticket) || _ticketProfiles.ContainsKey(ticket)
            || _activating.Contains(ticket) || !_issued.Remove(ticket)) return false;
        if(GroupSize>0)_batchRemaining++;
        _cursor = 0;
        return true;
    }

    private double Interval(int ticket)
    {
        unchecked
        {
            var hash=(uint)ticket*747796405u+Seed+2891336453u;
            hash=(hash^(hash>>16))*2246822519u;hash^=hash>>13;
            return Spacing*(1+Jitter*((hash%10001)/5000d-1));
        }
    }
    public bool TryActivate(int ticket,double now)
    {
        if(Ended||!Finite(now)||!_issued.Contains(ticket)||_spawned.Contains(ticket)||_activating.Contains(ticket))return false;
        if(Steady&&!_opening.Contains(ticket)&&(_activating.Count>0||now<_nextActivation))return false;
        _activating.Add(ticket);return true;
    }
    public bool Bind(int ticket,string profile)
    {
        if(Ended||!_issued.Contains(ticket)||string.IsNullOrEmpty(profile))return false;
        if(_profiles.TryGetValue(profile,out var old))return old==ticket;
        if(_ticketProfiles.ContainsKey(ticket))return false;
        _profiles.Add(profile,ticket);_ticketProfiles.Add(ticket,profile);return true;
    }
    public bool IsBound(string profile)=>_profiles.ContainsKey(profile);
    public int? TicketForProfile(string profile)=>_profiles.TryGetValue(profile,out var ticket)?ticket:null;
    public bool Created(string profile,double now=double.NaN)
    {
        if(Ended||!_profiles.TryGetValue(profile,out var ticket)||!_spawned.Add(ticket))return false;
        _activating.Remove(ticket);
        // Pace actual creation, not just HTTP/native request dispatch. A slow request
        // must not be followed immediately by a second, faster activation.
        if(Steady&&!_opening.Contains(ticket)&&Finite(now))_nextActivation=now+Interval(ticket);
        if(!_dead.Contains(profile))_alive.Add(profile);return true;
    }
    public bool Died(string profile)
    {
        if(Ended||!_profiles.ContainsKey(profile)||!_dead.Add(profile))return false;
        _alive.Remove(profile);return true;
    }
    public void End(){Ended=true;_batchRemaining=0;_activating.Clear();}
}

public static class WaveLimits
{
    public static bool IsFactory(string map) => map is "factory4_day" or "factory4_night";

    public static decimal FactoryPopulationMultiplier(decimal abundance, decimal roll)
    {
        var progress = (Math.Clamp(abundance, 1m, 3m) - 1m) / 2m;
        var expected = 1m + 1.5m * progress;
        return Math.Clamp(expected + (Math.Clamp(roll, 0m, 1m) - .5m) * .16m, 1m, 2.5m);
    }

    public static int FactoryPopulationCount(int baseline, decimal multiplier)
        => (int)Math.Round(baseline * multiplier, MidpointRounding.AwayFromZero);

    public static int FactoryActiveCap(int baseline, int total, int baselineTotal)
        => Math.Min(total, baseline + Math.Clamp((int)Math.Round((total / (double)baselineTotal - 1) * 7), 0, 10));

    public static int DefaultCap(string map)=>map.ToLowerInvariant() switch
    {"sandbox" or "sandbox_high"=>12,"bigmap"=>16,"interchange"=>20,_=>24};
    public static int Cap(string map,int configured,int total)=>Math.Min(Math.Max(1,total),Math.Max(1,Math.Min(64,configured>0?configured:DefaultCap(map))));
}
