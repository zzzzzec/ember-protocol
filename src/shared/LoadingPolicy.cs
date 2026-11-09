using System;
using System.Collections.Generic;
using System.Linq;
namespace Codex.ZombieSurvival.Rules;

public sealed class PreloadGroup
{
    public readonly string Role,Difficulty;
    public readonly int Count;
    public PreloadGroup(string role,string difficulty,int count){Role=role;Difficulty=difficulty;Count=count;}
}
public static class LoadingPolicy
{
    // A separate cache plan: never remove or mutate a ticket in the raid's spawn plan.
    public static IReadOnlyList<PreloadGroup>? Opening(IReadOnlyList<PreloadGroup> plan,int cap)
    {
        if(cap<1||plan.Count==0||plan.Any(p=>p.Count<0||(p.Count>0&&!p.Role.StartsWith("infected",StringComparison.OrdinalIgnoreCase))))return null;
        var first=new List<PreloadGroup>();var remaining=cap;
        foreach(var p in plan){if(p.Count==0)continue;if(remaining==0)break;var count=Math.Min(remaining,p.Count);first.Add(new PreloadGroup(p.Role,p.Difficulty,count));remaining-=count;}
        return first.GroupBy(p=>new{p.Role,p.Difficulty}).Select(g=>new PreloadGroup(g.Key.Role,g.Key.Difficulty,g.Sum(p=>p.Count))).ToArray();
    }
    public static IReadOnlyList<IReadOnlyList<PreloadGroup>> Batches(IReadOnlyList<PreloadGroup> groups,int limit)
    {
        if(limit<1||groups.Any(p=>p.Count<0))throw new ArgumentOutOfRangeException(nameof(limit));
        var result=new List<IReadOnlyList<PreloadGroup>>();var current=new List<PreloadGroup>();var used=0;
        foreach(var group in groups)
        {
            var left=group.Count;
            while(left>0)
            {
                var count=Math.Min(left,limit-used);current.Add(new PreloadGroup(group.Role,group.Difficulty,count));used+=count;left-=count;
                if(used==limit){result.Add(current);current=new List<PreloadGroup>();used=0;}
            }
        }
        if(current.Count>0)result.Add(current);return result;
    }
}
