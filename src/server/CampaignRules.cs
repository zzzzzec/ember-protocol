using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Codex.ZombieSurvival.Rules;
using SPTarkov.Server.Core.Models.Common;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private JsonObject _campaign=new();
    private readonly ConcurrentDictionary<string,JsonObject> _raidSpecs=new();
    private void LoadCampaign()=>_campaign=Read("data/campaign.json").AsObject();
    private HashSet<string> Completed(MongoId session)
    {
        if(!saves.GetProfiles().TryGetValue(session,out var profile))return [];
        return profile.CharacterData?.PmcData?.Quests?.Where(q=>(int)q.Status==4).Select(q=>q.QId.ToString()).ToHashSet()??[];
    }
    public JsonObject? BuildCampaignMap(MongoId session,string map)
    {
        if(Maps[map] is not JsonObject baseline)return null;
        var spec=baseline.DeepClone().AsObject();
        if((bool?)Config["campaignEnabled"]!=true)return spec;
        var completed=Completed(session);
        var crisis=_campaign["events"]!.AsArray().FirstOrDefault(e=>e?["map"]?.ToString()==map&&completed.Contains(e["starts"]!.ToString())&&!completed.Contains(e["ends"]!.ToString()));
        if(crisis is null)return spec;
        ResizePopulation(spec,(int)crisis["total"]!,(int)crisis["shooters"]!);
        spec["chest"]=(int)Math.Round((int)spec["chest"]!*(double)crisis["chestMultiplier"]!);
        spec["crisis"]=crisis["id"]!.ToString();spec["crisisName"]=crisis["name"]!.ToString();
        return spec;
    }
    private static void ResizePopulation(JsonObject spec,int total,int shooters)
    {
        var normal=spec["spawns"]!.AsArray().First(x=>x?["BossDifficult"]?.ToString()=="normal")!;
        var hard=spec["spawns"]!.AsArray().First(x=>x?["BossDifficult"]?.ToString()=="hard")!;
        var baselineSpawns=spec["spawns"]!.AsArray();var spawns=new JsonArray();
        var weights=baselineSpawns.GroupBy(x=>x!["BossZone"]!.ToString()).ToDictionary(g=>g.Key,g=>g.Count());
        var allocation=weights.ToDictionary(x=>x.Key,x=>x.Value*total/baselineSpawns.Count);
        foreach(var zone in weights.Keys.OrderByDescending(z=>weights[z]*total%baselineSpawns.Count).Take(total-allocation.Values.Sum()))allocation[zone]++;
        var assigned=weights.ToDictionary(x=>x.Key,_=>0);
        for(var i=0;i<total;i++)
        {
            var row=((i+1)*shooters/total>i*shooters/total?hard:normal).DeepClone();
            var zone=weights.Keys.MaxBy(z=>allocation[z]*(i+1)/(double)total-assigned[z])!;
            assigned[zone]++;row["BossZone"]=zone;
            spawns.Add(row);
        }
        spec["spawns"]=spawns;spec["total"]=total;spec["shooters"]=shooters;
        if(spec["density"] is JsonObject density)
        {
            var actual=new JsonObject();
            foreach(var row in spawns){var zone=row!["BossZone"]!.ToString();actual[zone]=((int?)actual[zone]??0)+1;}
            density["allocation"]=actual;
        }
    }
    private void CaptureRaidSpec(MongoId session,string map)
    {
        var spec=BuildCampaignMap(session,map)??throw new InvalidOperationException("地图未开放。");
        if(WaveLimits.IsFactory(map))
        {
            var abundance=AbundanceFor(session)["factory"].Total;
            var roll=RandomNumberGenerator.GetInt32(10001)/10000m;
            var multiplier=WaveLimits.FactoryPopulationMultiplier(abundance,roll);
            var total=WaveLimits.FactoryPopulationCount((int)spec["total"]!,multiplier);
            var shooters=WaveLimits.FactoryPopulationCount((int)spec["shooters"]!,multiplier);
            if(total!=(int)spec["total"]! || shooters!=(int)spec["shooters"]!)
                ResizePopulation(spec,total,shooters);
            spec["populationMultiplier"]=multiplier;
        }
        _raidSpecs[session.ToString()]=spec;
        RaidTraceStart(session,map);
        StartThreatPlan(session,map,spec);
        var multiplierLabel=spec["populationMultiplier"] is { } value?$"，本局数量倍率 ×{(decimal)value:0.000}":"";
        log.Info($"[余烬协议] {spec["name"]}: {(spec["crisisName"]?.ToString()??"常态")}，总量 {spec["total"]} 名感染者，{spec["shooters"]} 名持枪，同时上限 {WaveCap(map,(int)spec["total"]!)}{multiplierLabel}。",null);
    }
    internal int? FactoryRaidCap(MongoId session,string map)
        => WaveLimits.IsFactory(map) && _sessionMaps.GetValueOrDefault(session.ToString())==map &&
            _raidSpecs.TryGetValue(session.ToString(),out var spec) && spec["populationMultiplier"] is not null
            ? WaveCap(map,(int)spec["total"]!) : null;
    public JsonObject? MapSpecFor(MongoId session,string map)
    {
        if(_sessionMaps.GetValueOrDefault(session.ToString())==map&&_raidSpecs.TryGetValue(session.ToString(),out var spec))return spec;
        return BuildCampaignMap(session,map);
    }
}
