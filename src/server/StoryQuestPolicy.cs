using System.Globalization;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace Codex.ZombieSurvival;

public static class StoryQuestPolicy
{
    public static string CommitStage(JsonObject group)=>group["commitStage"]?.ToString()??"accept";
    public static bool RecoverChoice(JsonObject group,int status,double startTime)=>CommitStage(group)=="complete"?status==4:status is 2 or 3 or 4||status==5&&startTime>0;
    public static string PayloadHash(string payload)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    private static double Number(JsonNode? n,double fallback=0)=>double.TryParse(n?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:fallback;
    public static string? PrerequisiteError(JsonObject player,JsonObject quest)
    {
        foreach(var c in quest["conditions"]!["AvailableForStart"]!.AsArray())
        {
            if(c!["conditionType"]?.ToString()=="Level"&&Number(player["Info"]?["Level"])<Number(c["value"]))return "先达到这份委托要求的等级。";
            if(c["conditionType"]?.ToString()=="Quest"&&!((player["Quests"]?.AsArray()??[]).Any(q=>q?["qid"]?.ToString()==c["target"]?.ToString()&&Number(q["status"])==4)))return "先办完前面的委托。";
        }
        return null;
    }
    public static string? HandoverError(JsonObject player,JsonObject condition,JsonArray requested)
    {
        if(requested.Count==0)return "先选好要交付的物品。";
        var inventory=(player["Inventory"]?["items"]?.AsArray()??[]).OfType<JsonObject>().ToDictionary(i=>i["_id"]!.ToString());
        var targets=condition["target"]!.AsArray().Select(t=>t!.ToString()).ToHashSet();var seen=new HashSet<string>();var count=0d;
        foreach(var r in requested)
        {
            var id=r?["id"]?.ToString()??"";var amount=Number(r?["count"]);
            if(!seen.Add(id)||amount<=0||amount!=Math.Floor(amount))return "交付数量不正确。";
            if(!inventory.TryGetValue(id,out var item)||!targets.Contains(item["_tpl"]!.ToString()))return "这不是清单上的物品。";
            if(amount>Number(item["upd"]?["StackObjectsCount"],1))return "背包里没有这么多物品。";
            if((bool?)condition["onlyFoundInRaid"]==true&&(bool?)item["upd"]?["SpawnedInSession"]!=true)return "这份清单只收战局发现的物品。";
            count+=amount;
        }
        var progress=Number(player["TaskConditionCounters"]?[condition["id"]!.ToString()]?["value"]);
        if(count>Number(condition["value"])-progress)return "这些已经够了，多余的先留着。";
        return null;
    }
    public static bool ObjectivesReady(JsonObject player,JsonObject quest)=>quest["conditions"]!["AvailableForFinish"]!.AsArray().OfType<JsonObject>().All(c=>FieldQuestPolicy.ObjectiveReady(player,c,quest["_id"]!.ToString()));
}
