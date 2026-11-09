using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.ZombieSurvival.Rules;

internal static class ThreatChecks
{
    public static void Run(string root, string game, Action<bool,string> check)
    {
        var sight = new ThreatSight();
        check(!sight.Observe("a",true,0,1.2f) && !sight.Observe("a",true,.8f,1.2f) && sight.Observe("a",true,1.6f,1.2f),"irregular native sight retries eventually complete reaction");
        check(!sight.Observe("a",false,1.8f,1.2f) && !sight.Observe("a",true,2,1.2f),"losing confirmed sight clears acquisition without unseen tracking");
        check(!sight.Observe("b",true,4,1.2f) && sight.Observe("b",true,5.3f,1.2f),"new target starts its own reaction");
        check(!sight.Observe("b",true,9,1.2f),"expired visual sample cannot instantly reacquire");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<ThreatConfiguration>(File.ReadAllText(Path.Combine(root,"config/threat-generator.json")),options)!;
        var maps = JsonNode.Parse(File.ReadAllText(Path.Combine(root,"config/gameplay.json")))!["maps"]!.AsObject();
        var matrix = new List<object>(); var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (map,node) in maps)
        {
            var spent = new List<int>(); Dictionary<string,int>? example=null;
            for (int seed=1;seed<=24;seed++)
            {
                var plan=ThreatPolicy.Generate(config,map,(int)node!["rank"]!,(int)node["total"]!,(int)node["shooters"]!,seed);
                var again=ThreatPolicy.Generate(config,map,(int)node["rank"]!,(int)node["total"]!,(int)node["shooters"]!,seed);
                var strategy=config.Maps[map];
                check(plan.Units.Select(a=>(a.Key,a.WearCode)).SequenceEqual(again.Units.Select(a=>(a.Key,a.WearCode))),map+": seed reproduces composition "+seed);
                check(plan.Units.Count==(int)node["total"]!&&plan.Units.Count(a=>a.Armed)==(int)node["shooters"]!,map+": exact finite population and gunner quota "+seed);
                check(plan.Spent==plan.Units.Sum(a=>a.Score)&&plan.Spent<=plan.Budget,map+": scored plan never overspends "+seed);
                check(plan.RenderCost<=Math.Floor(plan.Units.Count*strategy.MaxRenderCostPerActor),map+": rendering budget remains bounded "+seed);
                check(plan.Units.Count(a=>a.Ordinary)>=Math.Ceiling((plan.Units.Count-(int)node["shooters"]!)*strategy.MinOrdinaryFraction),map+": ordinary-enemy floor "+seed);
                check(plan.Units.All(a=>a.MinRank<=(int)node["rank"]!&&a.Score<=strategy.MaxUnitScore&&a.Head<=40),map+": rank, unit ceiling and headshot thresholds "+seed);
                check(plan.Units.GroupBy(a=>a.Key).All(g=>g.Count()<=Math.Max(1,(int)Math.Floor(plan.Units.Count*g.First().MaxFraction))),map+": per-type quotas "+seed);
                check(plan.Units.All(a=>a.PlateFraction>=.15f&&a.PlateFraction<.70f),map+": random plates stay below seventy percent "+seed);
                spent.Add(plan.Spent);example??=plan.Units.GroupBy(a=>a.Id).ToDictionary(g=>g.Key,g=>g.Count());
            }
            matrix.Add(new{map,budget=config.Maps[map].Budget,minSpent=spent.Min(),maxSpent=spent.Max(),composition=example});
        }
        var code=ThreatPolicy.Encode("0123456789abcdef01234567",config.Revision,13);
        check(ThreatPolicy.TryDecode(code,config.Revision,out var key)&&key==13,"typed NPC identity survives ordinary profile serialization");
        check(!ThreatPolicy.TryDecode(code,config.Revision+1,out _)&&!ThreatPolicy.TryDecode("0123456789abcdef01234567",config.Revision,out _),"mismatched revisions and non-generated identities cannot acquire traits");
        var starter=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"content/server/data/profiles.json")))!["Standard"]!["usec"]!["character"]!["Inventory"]!["items"]!.AsArray();
        check(starter.Any(i=>i?["_tpl"]?.ToString()=="5e831507ea0a7c419c2f9bd9")&&starter.Any(i=>i?["_tpl"]?.ToString()=="544fb3364bdc2d34748b456a"),"new start includes one tourniquet and one splint without replacing old characters");
        var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"content/server/data/manifest.json")))!;
        var offers=manifest["offers"]!.AsArray();
        var pm=offers.First(o=>o?["tpl"]?.ToString()=="5448bd6b4bdc2dfc2f8b4569"&&(int?)o["level"]==1)!;
        var ammo=offers.First(o=>o?["tpl"]?.ToString()=="573719762459775a626ccbc1"&&(int?)o["level"]==1)!;
        check(pm["unlock"] is null&&ammo["unlock"] is null&&(int)pm["price"]!+24*(int)ammo["price"]!<=10000,"ungated PM plus three full magazines leaves recovery money from the 18000 RUB start");
        check((int)ammo["limit"]!>=90,"opening ammunition allowance supports imperfect aim against thirty infected");
        var templates=JsonNode.Parse(File.ReadAllText(Path.Combine(game,"SPT/SPT_Data/database/templates/items.json")))!.AsObject();
        foreach(var a in config.Archetypes)
            check((a.ArmorTpl.Length==0||templates.ContainsKey(a.ArmorTpl))&&(a.HelmetTpl.Length==0||templates.ContainsKey(a.HelmetTpl)),"native equipment templates exist: "+a.Id);
        Directory.CreateDirectory(Path.Combine(root,"artifacts"));
        File.WriteAllText(Path.Combine(root,"artifacts/threat-plan-matrix.json"),JsonSerializer.Serialize(new{passed=true,seedsPerMap=24,maps=matrix,milliseconds=clock.Elapsed.TotalMilliseconds,nativeDamageOrFpsAcceptance=false},new JsonSerializerOptions{WriteIndented=true}));
    }
}
