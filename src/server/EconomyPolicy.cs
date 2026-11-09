using System.Text.Json.Nodes;

namespace Codex.ZombieSurvival;

// One reference value per template, anchored to the cheapest complete cash offer.
// Attached parts retain their own value; the weapon root receives the remainder.
public static class EconomyPolicy
{
    public static Dictionary<string,double> RetailValuations(JsonObject trader, IReadOnlyDictionary<string,double> original, IReadOnlyDictionary<string,JsonObject> templates, ISet<string>? onlyTemplates=null)
    {
        var assort=trader["assort"]!;
        var items=assort["items"]!.AsArray().ToDictionary(x=>x!["_id"]!.ToString(),x=>x!);
        var children=items.Values.Where(x=>x["parentId"] is not null).GroupBy(x=>x["parentId"]!.ToString()).ToDictionary(g=>g.Key,g=>g.ToArray());
        var candidates=new Dictionary<string,List<(string Id,double Price)>>();
        foreach(var offer in assort["barter_scheme"]!.AsObject())
        {
            var schemes=offer.Value!.AsArray();
            if(schemes.Count!=1||schemes[0]!.AsArray().Count!=1)continue;
            var price=schemes[0]![0]!;
            if(price["_tpl"]!.ToString()!="5449016a4bdc2d6f028b456f")continue;
            var tpl=items[offer.Key]["_tpl"]!.ToString();
            if(onlyTemplates is not null&&!onlyTemplates.Contains(tpl))continue;
            if(!candidates.TryGetValue(tpl,out var list))candidates[tpl]=list=[];
            list.Add((offer.Key,(double)price["count"]!));
        }
        var resolved=new Dictionary<string,double>();var resolving=new HashSet<string>();
        double Descendants(string id,HashSet<string> visited)
        {
            if(!visited.Add(id))throw new InvalidDataException("Assort attachment cycle: "+id);
            var result=0d;
            if(children.TryGetValue(id,out var list))foreach(var child in list)
                result+=Resolve(child["_tpl"]!.ToString())*((double?)child["upd"]?["StackObjectsCount"]??1)+Descendants(child["_id"]!.ToString(),visited);
            return result;
        }
        double Resolve(string tpl)
        {
            if(resolved.TryGetValue(tpl,out var known))return known;
            if(!candidates.TryGetValue(tpl,out var offers))return original.GetValueOrDefault(tpl);
            if(!resolving.Add(tpl))throw new InvalidDataException("Retail reference cycle: "+tpl);
            var price=offers.Min(offer=>offer.Price-Descendants(offer.Id,[]));
            if(!double.IsFinite(price)||price<=0)throw new InvalidDataException("Complete retail offer underprices its parts: "+tpl);
            resolving.Remove(tpl);resolved[tpl]=price;return price;
        }
        foreach(var tpl in candidates.Keys)Resolve(tpl);
        // The client values the shell AND its child cartridges. Give packaging a
        // nominal positive value; valuing the shell as a full box would double-count ammo.
        foreach(var (tpl,item) in templates)
        {
            if(AmmoPackPolicy.Describe(templates,tpl) is { } pack&&resolved.ContainsKey(pack.Ammunition))
                resolved[tpl]=1;
        }
        return resolved;
    }
}
