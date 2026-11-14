using System.Text.Json;
using Path = System.IO.Path;
using System.Text.Json.Nodes;
using Codex.ZombieSurvival;
using Codex.ZombieSurvival.Rules;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;

var checks = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
{
    var practice = new ReloadPractice();
    Check(practice.Finish("first", true, true) == 0, "switching a magazine without firing earns no practice");
    practice.Fired("first");
    practice.Fired("first");
    practice.Fired("second");
    Check(practice.Finish("first", false, true) == 0, "cancelled reload does not spend fired rounds");
    Check(practice.Finish("first", true, false) == 0 && practice.HasRounds("first"), "a reload that loads nothing does not spend fired rounds");
    Check(practice.Finish("second", true, true) == 1 && practice.HasRounds("first"), "practice is tied to the firearm used");
    Check(practice.Finish("first", true, true) == 2, "successful reload consumes fired rounds once");
    Check(practice.Finish("first", true, true) == 0 && !practice.HasRounds("first"), "duplicate reload completion has no reward");
    practice.Fired("first");
    Check(new ReloadPractice().Finish("first", true, true) == 0, "a new raid starts without spent rounds");
}
MongoId tid = "111111111111111111111111", other = "222222222222222222222222", offer = "333333333333333333333333";
SptProfile Profile() => new() {
    CharacterData = new Characters { PmcData = new PmcData { TradersInfo = new() { [tid] = new TraderInfo { NextResupply = 0 } } } },
    SptData = new Spt(),
    TraderPurchases = new() { [tid] = new() { [offer] = new TraderPurchaseData { PurchaseCount = 6, PurchaseTimestamp = 1 } }, [other] = new() { [offer] = new TraderPurchaseData { PurchaseCount = 2 } } }
};
TraderAssort Full() => new() { Items = new() { new Item { Id = offer, Upd = new Upd { StackObjectsCount = 6, BuyRestrictionMax = 6, BuyRestrictionCurrent = 0 } } }, BarterScheme = new(), LoyalLevelItems = new() };
Trader Trader() => new() { Dialogue = new(), QuestAssort = new(), Base = new TraderBase { Id = tid, NextResupply = 0 }, Assort = new TraderAssort { Items = new() } };
foreach (var outcome in Enum.GetValues<ExitStatus>().Where(x => x != ExitStatus.TRANSIT))
{
    var p = Profile(); var trader = Trader();
    Check(RestockPolicy.Apply(p, trader, Full(), "raid-" + outcome, outcome), "return restocks for " + outcome);
    Check(!p.TraderPurchases!.ContainsKey(tid) && p.TraderPurchases[other][offer]!.PurchaseCount == 2, "only own merchant quota resets for " + outcome);
    Check(trader.Assort!.Items![0].Upd!.StackObjectsCount == 6 && p.CharacterData!.PmcData!.TradersInfo![tid].NextResupply == int.MaxValue, "stock and no-hourly clock restored for " + outcome);
}
{
    var p = Profile(); var trader = Trader(); var original = trader.Assort;
    Check(!RestockPolicy.Apply(p, trader, Full(), "transit", ExitStatus.TRANSIT) && ReferenceEquals(trader.Assort, original) && p.TraderPurchases!.ContainsKey(tid), "map transit does not restock");
    Check(!RestockPolicy.Apply(p, trader, Full(), "", ExitStatus.KILLED), "missing raid ID does not restock");
    Check(!RestockPolicy.Apply(p, trader, Full(), "bad", null), "missing result does not restock");
    Check(RestockPolicy.Apply(p, trader, Full(), "one", ExitStatus.KILLED), "first death refills");
    p.TraderPurchases![tid] = new() { [offer] = new TraderPurchaseData { PurchaseCount = 1 } };
    Check(!RestockPolicy.Apply(p, trader, Full(), "one", ExitStatus.KILLED) && p.TraderPurchases[tid][offer]!.PurchaseCount == 1, "duplicate return does not clear new purchases");
    var ledger = p.SptData!.ExtensionData!["emberRaidRestock"];
    p.SptData.ExtensionData["emberRaidRestock"] = JsonSerializer.SerializeToElement(ledger);
    Check(!RestockPolicy.Apply(p, trader, Full(), "one", ExitStatus.KILLED), "duplicate ID is remembered after JSON persistence");
    Check(RestockPolicy.Apply(p, trader, Full(), "two", ExitStatus.LEFT), "next aborted deployment refills");
    p.TraderPurchases[tid] = new() { [offer] = new TraderPurchaseData { PurchaseCount = 1 } };
    Check(!RestockPolicy.Apply(p, trader, Full(), "one", ExitStatus.KILLED) && p.TraderPurchases.ContainsKey(tid), "older replay does not refill after a later raid");
}
var settings = new HearingSettings();
Check(HearingPolicy.IsGroundZero("Sandbox") && HearingPolicy.IsGroundZero("sandbox_high") && !HearingPolicy.IsGroundZero("bigmap"), "actual mixed-case Ground Zero identifiers receive scaling");
float Gun(float power, bool centre = true, bool infected = false) => HearingPolicy.Radius(settings, "gun", power, centre, infected);
var gun = Gun(200); var quiet = HearingPolicy.Radius(settings, "silencedGun", 200, true, false);
Check(gun > 20 && gun <= 60, "ordinary centre gunshot stays local");
Check(Gun(10000) <= 60 && Gun(10000, false) <= 80, "even extreme native sound power cannot create a map-wide radius");
Check(quiet <= 15 && quiet < gun / 2, "suppressor has a substantial hearing advantage");
Check(Gun(200, true, true) < gun / 2, "infected gunfire cannot bridge the original full hearing radius");
Check(HearingPolicy.EffectiveRadius(settings, gun, true, 0) < 20 && gun > 20, "a listener twenty metres away hears in the open but not behind a wall");
Check(HearingPolicy.EffectiveRadius(settings, gun, true, 5) < HearingPolicy.EffectiveRadius(settings, gun, true, 0), "separate floors attenuate further");
Check(HearingPolicy.Radius(settings, "step", 100, true, false) <= 6, "movement is a close-range stimulus");
Check(Gun(float.NaN) == 0 && Gun(float.PositiveInfinity) == 0 && Gun(-1) == 0, "invalid native sound power is ignored");
Check(HearingPolicy.MemorySeconds("gun") == 18 && HearingPolicy.MemorySeconds("silencedGun") == 8, "sound investigation has bounded memory");
Check(HearingPolicy.ContinueCustomsBurst(7.9f,8,784)&&!HearingPolicy.ContinueCustomsBurst(8,8,784)&&!HearingPolicy.ContinueCustomsBurst(1,8,785), "Customs pressure is limited to one local burst");
Check(HearingPolicy.ApproachSector(0,1)==2&&HearingPolicy.ApproachSector(-1,0)==4&&HearingPolicy.ApproachSector(0,-1)==6, "separate approach bearings get staggered");
Check(HearingPolicy.CustomsResponseDelay(24,100,1,2,.5f)==0&&HearingPolicy.CustomsResponseDelay(50,100,1,2,.5f)<0, "only nearby infected join on the first unsuppressed shot");
Check(HearingPolicy.CustomsResponseDelay(50,100,2,0,0)>0&&HearingPolicy.CustomsResponseDelay(78,100,2,0,0)<0&&HearingPolicy.CustomsResponseDelay(78,100,3,0,0)>HearingPolicy.CustomsResponseDelay(50,100,2,0,1), "ongoing gunfire releases middle and outer approaches in order");
Check(HearingPolicy.CustomsWallResponse(.45f,1,0)==.45f &&
    HearingPolicy.CustomsWallResponse(.49f,2,1)==.56f && HearingPolicy.CustomsWallResponse(.53f,3,2)==.70f &&
    HearingPolicy.CustomsWallResponse(.53f,3,4)==.53f, "sustained Customs gunfire extends through walls on the same floor only");
var firstShot=HearingPolicy.CustomsGunfire(100,1);
var sustained=HearingPolicy.CustomsGunfire(100,3);
Check(Math.Abs(HearingPolicy.EffectiveRadius(new HearingSettings(),firstShot.Radius,true,0,firstShot.MinimumWallScale)-45)<.001
    && Math.Abs(HearingPolicy.EffectiveRadius(new HearingSettings(),sustained.Radius,true,0,sustained.MinimumWallScale)-66.25)<.001
    && HearingPolicy.CustomsGunfire(100,20)==sustained, "local Customs gunfire builds bounded pressure through cover");
Check(AwarenessPolicy.CuePriority("sighting")>AwarenessPolicy.CuePriority("gun") && AwarenessPolicy.CuePriority("gun")>AwarenessPolicy.CuePriority("step"), "weaker cues cannot pull a Customs infected off contact");
{
    var legacy = new JsonObject { ["BodyParts"] = new JsonObject(), ["Energy"] = new JsonObject { ["Current"] = 30 } };
    var parts = legacy["BodyParts"]!.AsObject();
    foreach (var entry in new Dictionary<string, double> { ["Head"] = 150, ["Chest"] = 200, ["Stomach"] = 150, ["LeftArm"] = 100, ["RightArm"] = 100, ["LeftLeg"] = 125, ["RightLeg"] = 125 })
        parts[entry.Key] = new JsonObject { ["Health"] = new JsonObject { ["Maximum"] = entry.Value, ["Current"] = entry.Value * .2 }, ["Effects"] = new JsonObject { ["HeavyBleeding"] = new JsonObject { ["Time"] = -1 }, ["Fracture"] = new JsonObject { ["Time"] = -1 } } };
    var injuries = parts.ToDictionary(x => x.Key, x => x.Value!["Effects"]!.ToJsonString());
    Check(LegacyHealthPolicy.Normalize(legacy), "known old SVM health is migrated");
    Check(parts.Sum(x => (double)x.Value!["Health"]!["Maximum"]!) == 440, "new maximum is exactly standard 440");
    Check(parts.All(x => Math.Abs((double)x.Value!["Health"]!["Current"]! / (double)x.Value["Health"]!["Maximum"]! - .2) < 1e-8), "each body part keeps its current health percentage");
    Check(parts.All(x => x.Value!["Effects"]!.ToJsonString() == injuries[x.Key]) && (int)legacy["Energy"]!["Current"]! == 30, "bleeding fractures and unrelated health state are unchanged");
    var once = legacy.ToJsonString();
    Check(!LegacyHealthPolicy.Normalize(legacy) && legacy.ToJsonString() == once, "standard health is not repeatedly rescaled");
    var foreign = legacy.DeepClone().AsObject(); foreign["BodyParts"]!["Head"]!["Health"]!["Maximum"] = 50;var before = foreign.ToJsonString();
    Check(!LegacyHealthPolicy.Normalize(foreign) && foreign.ToJsonString() == before, "other custom health presets are not silently replaced");
}
var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(args[0], "content/server/data/manifest.json")))!;
var traderData = JsonNode.Parse(File.ReadAllText(Path.Combine(args[0], "content/server/data/trader.json")))!;
var medical = new Dictionary<string, int> { ["759076688ecde0eab1c4b6d9"] = 3, ["afbec67057c47f1e93fb1ce0"] = 6, ["363bee7173ce6c4203082758"] = 6, ["ec65cda4378b36df52c19994"] = 3 };
foreach (var (id, limit) in medical)
{
    var o = manifest["offers"]!.AsArray().Single(x => x!["id"]!.ToString() == id)!;
    var item = traderData["assort"]!["items"]!.AsArray().Single(x => x!["_id"]!.ToString() == id)!;
    Check((int)o["level"]! == 1 && o["unlock"] is null && (int)o["limit"]! == limit && (int)o["stock"]! == limit && (int)item["upd"]!["BuyRestrictionMax"]! == limit && (int)item["upd"]!["StackObjectsCount"]! == limit, "level-one medical quota agrees in manifest and real assort: " + id);
}
Check(AwarenessPolicy.InSightCone(20,1,25,100), "a stationary player in clear forward view is eligible without a native enemy flag");
Check(!AwarenessPolicy.InSightCone(26,1,25,100), "an infected cannot see past its archetype's range");
Check(!AwarenessPolicy.InSightCone(20,-1,25,100), "a player behind an infected is outside its view");
Check(!AwarenessPolicy.InSightCone(20,.55f,25,100)&&AwarenessPolicy.InSightCone(20,.55f,35,120), "archetype angles affect peripheral detection");
Check(AwarenessPolicy.InSightCone(2,-1,25,100), "contact inside melee range can use the obstruction check from any angle");
Check(!AwarenessPolicy.ChaseDestinationMoved(2.24f)&&AwarenessPolicy.ChaseDestinationMoved(2.25f), "a visible chase repaths once the player is at least 1.5 metres from its current route destination");
Check(!AwarenessPolicy.InformationAlive(100,99) && AwarenessPolicy.InformationAlive(100,101), "old information expires instead of following a live target");
Check(!AwarenessPolicy.AcceptCue("sighting","gun",.4f,false) &&
      AwarenessPolicy.AcceptCue("sighting","gun",1.25f,false) &&
      !AwarenessPolicy.AcceptCue("sighting","gun",10,true), "recent sightings resist weaker cues while Customs retains its existing priority");
Check(AwarenessPolicy.AcceptCue("gun","sighting",0,false), "a directly seen player takes priority over a sound");
Check(AwarenessPolicy.AcceptCue("sighting", "gun", .2f, true, nextPlayerShot: true),
    "a newly heard player shot supersedes a stored Customs sighting");
Check(AwarenessPolicy.AcceptCue("explosion", "silencedGun", .2f, true, nextPlayerShot: true) &&
      AwarenessPolicy.AcceptCue("gun", "silencedGun", .2f, true, previousPlayerShot: true, nextPlayerShot: true),
    "the latest audible player shot wins over a louder previous location");
Check(!AwarenessPolicy.AcceptCue("gun", "step", 1.9f, false, previousPlayerShot: true) &&
      !AwarenessPolicy.AcceptCue("gun", "local_sighting", 1.9f, false, previousPlayerShot: true) &&
      !AwarenessPolicy.AcceptCue("gun", "explosion", 1.9f, false, previousPlayerShot: true),
    "other noise cannot divert an infected from recent player gunfire");
Check(AwarenessPolicy.AcceptCue("gun", "sighting", 0, true, previousPlayerShot: true) &&
      AwarenessPolicy.AcceptCue("gun", "contact", 0, true, previousPlayerShot: true),
    "current sight and contact still take over from player gunfire");
Check(AwarenessPolicy.AcceptCue("gun", "local_sighting", 2f, false, previousPlayerShot: true),
    "gunfire priority fades after shooting stops");
{
    var turn = new InfectedTurnBudget();
    Check(turn.Limit(1, 0, 1f/60, false) == 1 && turn.Limit(-90, 0, 1f/60, false) == -1 &&
          turn.Limit(90, 0, 1f/60, false) == 0, "all idle yaw requests in the same frame share one limit");
    Check(Math.Abs(turn.Limit(90, 1, 1f/60, true) - 3.5f) < .001f, "moving yaw has a faster limit");
    Check(Math.Abs(turn.Limit(-180, 2, 1, false) + 6f) < .001f, "a frame hitch cannot cause a snap turn");
    Check(Math.Abs(turn.Limit(180, 3, 1f/30, false) - 4f) < .001f, "the turn limit scales with frame duration");
    Check(turn.Limit(90,4,1f/60,false,consume:false)==2 &&
          turn.Limit(90,4,1f/60,false)==2 && turn.Limit(90,4,1f/60,false)==0,
          "steering can preview the same turn limit that player rotation consumes");
}
Check(AwarenessPolicy.LocalDensity(10,25)>AwarenessPolicy.LocalDensity(5,25), "local density compares equal-radius encounters");
var rules = JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"content/server/data/field-quests.json")))!;
var allQuests = JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"content/server/data/quests.json")))!;
var stockItems = JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"content/server/data/trader.json")))!["assort"]!["items"]!.AsArray();
var nativeItems = JsonNode.Parse(File.ReadAllText(Path.Combine(args[2],"SPT/SPT_Data/database/templates/items.json")))!.AsObject().ToDictionary(x=>x.Key,x=>x.Value!.AsObject());
foreach(var loan in rules["loans"]!.AsObject())
{
    var spec=loan.Value!.AsObject(); var quest=allQuests[loan.Key]!;
    var issue=quest["rewards"]!["Started"]!.AsArray().SingleOrDefault(x=>x!["type"]?.ToString()=="Item"&&x["items"]![0]!["_tpl"]!.ToString()==spec["weapon"]!.ToString());
    var tree=issue?["items"]?.DeepClone().AsArray();
    if(tree is null)
    {
        var referenceOffer=spec["referenceOffer"]!.ToString();
        var selected=new HashSet<string>{referenceOffer};
        tree=[];
        foreach(var item in stockItems)
            if(selected.Contains(item!["_id"]!.ToString()) || selected.Contains(item["parentId"]?.ToString()??""))
            { selected.Add(item["_id"]!.ToString()); tree.Add(item.DeepClone()); }
    }
    var rootId=tree[0]!["_id"]!.ToString();
    var error=FieldQuestPolicy.ValidateReturn(spec,tree,rootId,nativeItems);
    Check(error is null, "issued rifle is a compatible complete return: "+spec["weapon"]+" "+error);
    var replacement=tree.DeepClone().AsArray();
    foreach(var item in replacement){if(item!["parentId"]?.ToString()==rootId)item["parentId"]="aaaaaaaaaaaaaaaaaaaaaaaa";}
    replacement[0]!["_id"]="aaaaaaaaaaaaaaaaaaaaaaaa";
    Check(FieldQuestPolicy.ValidateReturn(spec,replacement,"aaaaaaaaaaaaaaaaaaaaaaaa",nativeItems) is null,"a different rifle instance is accepted: "+spec["weapon"]);
    var worn=tree.DeepClone().AsArray();worn[0]!["upd"]!["Repairable"]!["Durability"]=49;
    Check(FieldQuestPolicy.ValidateReturn(spec,worn,rootId,nativeItems) is not null,"low-durability rifle is rejected: "+spec["weapon"]);
    var bare=tree.DeepClone().AsArray();
    var mag=bare.FirstOrDefault(x=>x!["slotId"]?.ToString()=="mod_magazine")??bare[1]!;
    bare.Remove(mag);
    Check(FieldQuestPolicy.ValidateReturn(spec,bare,rootId,nativeItems) is not null,"missing magazine or integral part is rejected: "+spec["weapon"]);
    foreach(var required in spec["requiredParts"]!.AsArray())
    {
        var missing = new JsonArray(tree.Where(item => item!["_tpl"]?.ToString() != required!.ToString())
            .Select(item => item!.DeepClone()).ToArray());
        Check(FieldQuestPolicy.ValidateReturn(spec,missing,rootId,nativeItems) is not null,"missing required part is rejected: "+required);
    }
    var loaded = tree.DeepClone().AsArray();
    foreach (var part in tree)
    {
        var props = nativeItems[part!["_tpl"]!.ToString()]["_props"]!;
        foreach (var group in new[] { "Chambers", "Cartridges" })
        {
            var slot = props[group]?.AsArray().FirstOrDefault();
            var round = slot?["_props"]?["filters"]?[0]?["Filter"]?.AsArray()
                .FirstOrDefault(id => nativeItems.TryGetValue(id!.ToString(), out var template) && template["_props"]?["ammoType"] is not null);
            if (round is null) continue;
            var ammo = new JsonObject { ["_id"] = Guid.NewGuid().ToString("N")[..24], ["_tpl"] = round.ToString(),
                ["parentId"] = part["_id"]!.ToString(), ["slotId"] = slot!["_name"]!.ToString(),
                ["upd"] = new JsonObject { ["StackObjectsCount"] = 1 } };
            if (group == "Cartridges") ammo["location"] = 0;
            loaded.Add(ammo);
        }
    }
    Check(FieldQuestPolicy.ValidateReturn(spec, loaded, rootId, nativeItems) is null,
        "compatible ammunition in the gun does not block the return: " + spec["weapon"]);
    if (spec["weapon"]!.ToString() == "5cadc190ae921500103bb3b6")
    {
        loaded.Add(new JsonObject { ["_id"] = "bbbbbbbbbbbbbbbbbbbbbbbb", ["_tpl"] = "5cc9c20cd7f00c001336c65d",
            ["parentId"] = rootId, ["slotId"] = "mod_tactical" });
        Check(FieldQuestPolicy.ValidateReturn(spec, loaded, rootId, nativeItems) is null,
            "the M9A3 trial accepts a loaded build with an extra compatible tactical attachment");
    }
    var c=quest["conditions"]!["AvailableForFinish"]![0]!.AsObject();var actor=new JsonObject{["TaskConditionCounters"]=new JsonObject(),["Quests"]=new JsonArray()};
    Check(!FieldQuestPolicy.ObjectiveReady(actor,c,loan.Key),"missing kill counter prevents early return: "+spec["weapon"]);
    actor["TaskConditionCounters"]![c["id"]!.ToString()]=new JsonObject{["value"]=c["value"]!.DeepClone()};
    Check(FieldQuestPolicy.ObjectiveReady(actor,c,loan.Key),"persisted exact kill counter permits objective: "+spec["weapon"]);
}
var mapSettings=JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"config/gameplay.json")))!["maps"]!;
Check((int)mapSettings["sandbox"]!["total"]! ==30 && (int)mapSettings["sandbox_high"]!["total"]! ==30,"both centre variants have thirty fixed infected");
Check((int)mapSettings["sandbox"]!["shooters"]! ==1,"centre retains one armed infected");
foreach(var entry in mapSettings.AsObject())
{
    var spec=entry.Value!;var pool=spec["spawns"]!.AsArray();
    Check(pool.Count==(int)spec["total"]!&&pool.Count(x=>x!["BossName"]!.ToString()=="infectedPmc")== (int)spec["shooters"]!,"fixed pool and armed count agree: "+entry.Key);
    Check(pool.All(x=>(int)x!["Time"]! ==-1),"all infected belong to the opening pool: "+entry.Key);
    var actual=pool.GroupBy(x=>x!["BossZone"]!.ToString()).ToDictionary(g=>g.Key,g=>g.Count());
    Check(spec["density"]!["allocation"]!.AsObject().All(x=>actual.GetValueOrDefault(x.Key)==(int)x.Value!),"density metadata matches actual authored pool: "+entry.Key);
}
var events=JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"content/server/data/campaign.json")))!["events"]!.AsArray();
foreach(var e in events)Check((int)e!["total"]!>(int)mapSettings[e["map"]!.ToString()]!["total"]!,"crisis must exceed its revised normal map: "+e["id"]);
var originalValues=JsonNode.Parse(File.ReadAllText(Path.Combine(args[2],"SPT/SPT_Data/database/templates/handbook.json")))!["Items"]!.AsArray().ToDictionary(x=>x!["Id"]!.ToString(),x=>(double)x!["Price"]!);
var ammoEconomy=JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"config/gameplay.json")))!["economy"]!;
foreach(var (tpl,price) in ammoEconomy["handbookPrices"]!.AsObject())originalValues[tpl]=(double)price!;
var retailValues=EconomyPolicy.RetailValuations(traderData.AsObject(),originalValues,nativeItems,JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"config/gameplay.json")))!["economy"]!["retailTemplates"]!.AsArray().Select(x=>x!.ToString()).ToHashSet());
var pricedItems=traderData["assort"]!["items"]!.AsArray();
double PricedTree(string id,bool root=true)
{
    var item=pricedItems.Single(x=>x!["_id"]!.ToString()==id)!;var tpl=item["_tpl"]!.ToString();
    var value=retailValues.GetValueOrDefault(tpl,originalValues.GetValueOrDefault(tpl))*(root?1:(double?)item["upd"]?["StackObjectsCount"]??1);
    return value+pricedItems.Where(x=>x!["parentId"]?.ToString()==id).Sum(x=>PricedTree(x!["_id"]!.ToString(),false));
}
Check(retailValues.Values.All(x=>double.IsFinite(x)&&x>0),"all unified reference values are finite and positive");
foreach(var auditOffer in manifest["offers"]!.AsArray().Where(x=>(double)x!["price"]!>0))
{
    var total=PricedTree(auditOffer!["id"]!.ToString());
    Check(total*.4<(double)auditOffer["price"]!,"whole cash offer cannot be resold at a profit: "+auditOffer["id"]);
}
var toz=manifest["offers"]!.AsArray().First(x=>x!["tpl"]!.ToString()=="5a38e6bac4a2826c6e06d79b"&&(double)x["price"]!>0)!;
Check(Math.Abs(PricedTree(toz["id"]!.ToString())-9000)<.01,"complete TOZ reference equals our 9000 retail price, including attachments");
Check(originalValues["573719762459775a626ccbc1"]==manifest["offers"]!.AsArray().Where(o=>o!["tpl"]!.ToString()=="573719762459775a626ccbc1"&&(double)o["price"]!>0).Min(o=>(double)o!["price"]!),"PM recovery reference equals its cheapest authored cash channel");
var basicPm=manifest["offers"]!.AsArray().Single(o=>o!["id"]!.ToString()=="3ee351b902a613fdb800c9fc")!;
Check((double)basicPm["price"]!*24<=2500,"twenty-four basic PM rounds remain affordable for self-defense");
var firstTierAmmo=manifest["offers"]!.AsArray().Where(o=>(int)o!["level"]! ==1&&o["unlock"] is null&&(double)o["price"]!>0&&nativeItems.GetValueOrDefault(o["tpl"]!.ToString())?["_props"]?["ammoType"] is not null).Sum(o=>(double)o!["price"]!*(int)o["limit"]!);
Check(firstTierAmmo>36000,"buying every first-tier ammunition type exceeds two starting cash grants");
AmmoChecks.Run(nativeItems,Check);
ThreatChecks.Run(args[0],args[2],Check);
var storyRules=JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"content/server/data/story-quests.json")))!;
{
    var legacy=new JsonObject();var confirmed=new JsonObject{["commitStage"]="complete"};
    Check(StoryQuestPolicy.CommitStage(legacy)=="accept","legacy transport keeps acceptance commitment");
    foreach(var status in new[]{0,1,2,3,5,6,7})Check(!StoryQuestPolicy.RecoverChoice(confirmed,status,100),"prepared, failed or cancelled drafts cannot recover as sent: "+status);
    Check(StoryQuestPolicy.RecoverChoice(confirmed,4,100),"only native Success recovers a completion-confirmed reply");
    Check(StoryQuestPolicy.RecoverChoice(legacy,2,100)&&StoryQuestPolicy.RecoverChoice(legacy,5,100),"legacy commitment survives administrative failure");
    Check(!StoryQuestPolicy.RecoverChoice(legacy,5,0),"never-accepted opposing transport is not a commitment");
    Check(StoryQuestPolicy.PayloadHash("abc")=="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","send receipt uses standard UTF-8 SHA256");
}
foreach(var id in storyRules["quests"]!.AsArray())
{
    var quest=allQuests[id!.ToString()]!;
    Check(quest["rewards"]!["Started"]!.AsArray().Count==0,"story acceptance cannot issue repeatable supplies: "+id);
    foreach(var c in quest["conditions"]!["AvailableForFinish"]!.AsArray().Where(c=>c!["conditionType"]?.ToString()=="HandoverItem"))
        Check(c!["target"]!.AsArray().All(t=>nativeItems.ContainsKey(t!.ToString())),"story handover refers to actual item templates: "+c!["id"]);
}
{
    var quest=allQuests[storyRules["questKeys"]!["repair1"]!.ToString()]!.AsObject();
    var c=quest["conditions"]!["AvailableForFinish"]![0]!.AsObject();var tpl=c["target"]![0]!.ToString();
    var player=new JsonObject{["Info"]=new JsonObject{["Level"]=30},["Quests"]=new JsonArray(),["TaskConditionCounters"]=new JsonObject(),["Inventory"]=new JsonObject{["items"]=new JsonArray(new JsonObject{["_id"]="a",["_tpl"]=tpl,["upd"]=new JsonObject{["StackObjectsCount"]=1,["SpawnedInSession"]=false}})}};
    var valid=new JsonArray(new JsonObject{["id"]="a",["count"]=1});
    Check(StoryQuestPolicy.PrerequisiteError(player,quest) is not null,"story prerequisites cannot be bypassed by accepting a locked quest");
    foreach(var gate in quest["conditions"]!["AvailableForStart"]!.AsArray().Where(g=>g!["conditionType"]?.ToString()=="Quest"))player["Quests"]!.AsArray().Add(new JsonObject{["qid"]=gate!["target"]!.ToString(),["status"]=4});
    Check(StoryQuestPolicy.PrerequisiteError(player,quest) is null,"actual completed prerequisites unlock story acceptance");
    player["Info"]!["Level"]=1;Check(StoryQuestPolicy.PrerequisiteError(player,quest) is not null,"story level requirement is also enforced server-side");
    Check(StoryQuestPolicy.HandoverError(player,c,valid) is not null,"purchased non-FIR items cannot satisfy a FIR story handover");
    player["Inventory"]!["items"]![0]!["upd"]!["SpawnedInSession"]=true;
    Check(StoryQuestPolicy.HandoverError(player,c,valid) is null,"exact FIR target is accepted");
    Check(StoryQuestPolicy.HandoverError(player,c,new JsonArray()) is not null,"empty handover is rejected");
    Check(StoryQuestPolicy.HandoverError(player,c,new JsonArray(new JsonObject{["id"]="a",["count"]=0})) is not null,"zero handover quantity is rejected");
    Check(StoryQuestPolicy.HandoverError(player,c,new JsonArray(new JsonObject{["id"]="a",["count"]=2})) is not null,"handover cannot exceed the actual stack");
    Check(StoryQuestPolicy.HandoverError(player,c,new JsonArray(valid[0]!.DeepClone(),valid[0]!.DeepClone())) is not null,"one item cannot be counted twice in a handover");
    player["Inventory"]!["items"]![0]!["_tpl"]="5449016a4bdc2d6f028b456f";
    Check(StoryQuestPolicy.HandoverError(player,c,valid) is not null,"wrong template cannot be handed over");
    Check(!StoryQuestPolicy.ObjectivesReady(player,quest),"story completion waits for all actual objective counters");
}
// The same planner used by the Unity adapter: crowded spawns, stale information and horde convergence.
{
    Check(PatrolPolicy.Setting(float.NaN,8,45,24)==24&&PatrolPolicy.Setting(float.PositiveInfinity,8,45,24)==24,"invalid configuration falls back to a finite patrol radius");
    Check(PatrolPolicy.Setting(-100,8,45,24)==8&&PatrolPolicy.Setting(500,8,45,24)==45,"configuration cannot create unbounded idle roaming");
    var patrolSettings=new PatrolSettings();var empty=Array.Empty<RoamPoint>();var home=new RoamPoint(0,0,0);
    var plans=new List<PatrolPlan>();var occupied=new List<RoamPoint>();
    for(var n=0;n<30;n++)
    {
        var plan=new PatrolPlan(PatrolPolicy.Seed("crowded-infected-"+n),home);
        plan.Update(patrolSettings,1,home,null,0,occupied);plans.Add(plan);occupied.Add(plan.Centre);
        Check(plan.Centre.Finite&&plan.Centre.FlatDistanceSquared(home)<=patrolSettings.Radius*patrolSettings.Radius+.01f,"idle centre remains in local area "+n);
        Check(plan.HoldSeconds>=10&&plan.HoldSeconds<=24,"idle timer remains bounded "+n);
    }
    var worst=occupied.Max(a=>occupied.Count(b=>a.FlatDistanceSquared(b)<patrolSettings.Spacing*patrolSettings.Spacing));
    Check(worst<=4,"thirty coincident spawns do not choose a common five-actor idle centre; max nearby="+worst);
    Check(plans.Select(p=>p.HoldSeconds).Distinct().Count()>25,"idle actors have independently staggered timers");
    var sample=plans[0];var revision=sample.Revision;var centre=sample.Centre;
    for(var n=0;n<10000;n++)sample.Update(patrolSettings,1.1f,home,null,0,empty);
    Check(sample.Revision==revision&&sample.Centre.FlatDistanceSquared(centre)==0,"frequent native getters do not trigger extra scans or new idle plans");
    var cue=new RoamPoint(100,2,70);
    foreach(var plan in plans)
    {
        plan.Update(patrolSettings,2,home,cue,20,occupied);
        Check(plan.FollowingInformation&&plan.Centre.FlatDistanceSquared(cue)==0,"valid cue overrides idle spacing and permits a local horde");
    }
    sample=plans[0];revision=sample.Revision;
    Check(!sample.Update(patrolSettings,2.2f,home,new RoamPoint(cue.X+1,cue.Y,cue.Z),20,occupied) &&
          sample.Centre.FlatDistanceSquared(cue)==0&&sample.HoldSeconds>=17.8f,
          "small changes retain the last navigation point until the investigation expires");
    var newSighting = new RoamPoint(cue.X+1,cue.Y,cue.Z);
    Check(sample.Update(patrolSettings,2.4f,home,newSighting,20,occupied,4) &&
          sample.Centre.FlatDistanceSquared(newSighting)==0,"a stronger sighting replaces a nearby weaker cue");
    revision=sample.Revision;
    for(var n=1;n<=60;n++)sample.Update(patrolSettings,3+n*.1f,home,new RoamPoint(cue.X+n*.1f,cue.Y,cue.Z),20,occupied,4);
    Check(sample.Revision>revision,"successive small cue movements eventually invalidate the stale native point");
    var shooter = new PatrolPlan(17, home);
    shooter.Update(patrolSettings, 1, home, cue, 20, empty, 4);
    var latestShot = new RoamPoint(cue.X + 2.5f, cue.Y, cue.Z);
    Check(shooter.Update(patrolSettings, 1.2f, home, latestShot, 19, empty, 3, playerGunfire: true) &&
          shooter.Centre.FlatDistanceSquared(latestShot) == 0,
        "a newly heard shot inside the old four-metre threshold invalidates the previous sighting route");
    revision = shooter.Revision;
    for (var n = 0; n < 10; n++)
        shooter.Update(patrolSettings, 1.4f + n * .2f, home, latestShot, 20, empty, 3, playerGunfire: true);
    Check(shooter.Revision == revision, "repeated shots from the same spot do not restart the path");
    var nextFloor = new RoamPoint(latestShot.X, latestShot.Y + 2, latestShot.Z);
    Check(shooter.Update(patrolSettings, 3.5f, home, nextFloor, 20, empty, 3, playerGunfire: true) &&
          shooter.Centre.Y == nextFloor.Y, "a newly audible shot on a different landing invalidates the old path");
    foreach(var plan in plans)
    {
        var arrived=new RoamPoint(98,2,69);plan.Update(patrolSettings,21,arrived,cue,20,empty);
        Check(!plan.FollowingInformation&&plan.Home.FlatDistanceSquared(arrived)==0,"expired information resumes local roaming around the actual reached location");
    }
    var invalid=new PatrolPlan(1,home);invalid.Update(patrolSettings,1,home,new RoamPoint(float.NaN,0,0),10,empty);
    Check(!invalid.FollowingInformation&&invalid.Centre.Finite,"nonfinite cue is not a destination");
    var expired=new PatrolPlan(2,home);expired.Update(patrolSettings,1,home,cue,0,empty);
    Check(!expired.FollowingInformation,"a location without a live cue cannot attract infected");
    var baseline=PatrolPolicy.Choose(patrolSettings,42,1,home,home,empty);
    var upstairs=PatrolPolicy.Choose(patrolSettings,42,1,home,home,new[]{new RoamPoint(baseline.X,10,baseline.Z)});
    Check(baseline.FlatDistanceSquared(upstairs)==0,"different floors do not repel idle actors");
    var again=new PatrolPlan(PatrolPolicy.Seed("crowded-infected-0"),home);again.Update(patrolSettings,1,home,null,0,empty);
    Check(again.Centre.FlatDistanceSquared(occupied[0])==0,"stable actor identity creates reproducible initial destinations");
}
{
    Item I(string id,string tpl,string? parent=null)=>new(){Id=(MongoId)id,Template=(MongoId)tpl,ParentId=parent is null?null:(MongoId)parent};
    var bag="111111111111111111111111";var cash="222222222222222222222222";var ordinary="333333333333333333333333";
    var inventory=new[]{I(bag,"544a5caa4bdc2d1a388b4568"),I(cash,TraderSalePolicy.Roubles,bag),I(ordinary,"544fb25a4bdc2dfb738b4567")};
    Check(TraderSalePolicy.ContainsSettlementMoney(inventory,new[]{cash}),"rouble stacks cannot be sold as goods");
    Check(TraderSalePolicy.ContainsSettlementMoney(inventory,new[]{bag}),"a bag containing roubles cannot silently sell its cash");
    Check(TraderSalePolicy.ContainsSettlementMoney(inventory,new[]{ordinary,cash}),"mixed sale is rejected before any ordinary item can be removed");
    Check(!TraderSalePolicy.ContainsSettlementMoney(inventory,new[]{ordinary}),"ordinary goods remain sellable when cash is elsewhere");
    Check(!TraderSalePolicy.ContainsSettlementMoney(new[]{I(cash,"569668774bdc2da2298b4568")},new[]{cash}),"foreign currency exchange remains available");
}
// Actual creation can lag requests and then complete together. Exercise the same
// admission/accounting code linked into the runtime, with different native delays.
foreach(var spec in new[]{(Total:30,Cap:12),(Total:120,Cap:36),(Total:110,Cap:20),(Total:230,Cap:24)})
foreach(var latency in new[]{.2,4d,9d})
{
    var steady=new WaveBudget(spec.Cap,2,4,6,true,2,.25,12345);
    for(var i=0;i<spec.Total;i++){Check(steady.Register(i),"steady fixed ticket registered");steady.Opening(i);}
    steady.Seal();var live=new Queue<string>();
    for(var i=0;i<spec.Cap;i++)
    {
        var id="steady-"+i;Check(steady.Bind(i,id)&&steady.TryActivate(i,0)&&steady.Created(id,0),"initial capped population activates normally");live.Enqueue(id);
    }
    var reserved=new Dictionary<int,double>();var loading=new Dictionary<int,double>();
    var lastBirth=double.NegativeInfinity;var nextKill=1d;var burstKilled=false;
    for(var tick=0;tick<30000&&steady.Spawned<spec.Total;tick++)
    {
        var now=tick*.2;
        foreach(var item in loading.Where(x=>x.Value<=now).ToArray())
        {
            Check(now-lastBirth>=4.5-.0001,"slow and fast native completions never form a reinforcement burst");
            Check(steady.Created("steady-"+item.Key,now)&&!steady.Created("steady-"+item.Key,now),"reinforcement creation is counted exactly once");
            lastBirth=now;live.Enqueue("steady-"+item.Key);loading.Remove(item.Key);reserved.Remove(item.Key);
        }
        if(now>=10&&!burstKilled)
        {
            for(var n=0;n<8&&live.Count>0;n++)steady.Died(live.Dequeue());
            burstKilled=true;
        }
        if(now>=nextKill&&live.Count>0){steady.Died(live.Dequeue());nextKill=now+5;}
        var ticket=steady.TakeNext(now);
        if(ticket.HasValue)
        {
            Check(steady.Bind(ticket.Value,"steady-"+ticket.Value),"native request binds once to its original profile");
            reserved.Add(ticket.Value,now+(ticket.Value%3==0?9:0));
        }
        foreach(var item in reserved.Where(x=>x.Value<=now&&!loading.ContainsKey(x.Key)).ToArray())
            if(steady.TryActivate(item.Key,now))loading.Add(item.Key,now+(item.Key%2==0?latency:.2));
        Check(steady.Alive+steady.Pending<=spec.Cap,"steady accounting includes deferred and in-flight requests");
        Check(steady.Pending<=2&&steady.Activating<=1,"reinforcement queue and native activation are bounded");
    }
    Check(steady.Spawned==spec.Total&&steady.Issued==spec.Total,"steady finite reserve is exhausted exactly once");
    while(live.Count>0)steady.Died(live.Dequeue());
    Check(steady.Cleared&&steady.TakeNext(100000) is null,"steady replenishment stops after the authored total is killed");
    steady.End();Check(!steady.TryActivate(0,100000),"late native activation is blocked after raid teardown");
}
{
    var steady=new WaveBudget(12,2,4,6,true,2,.25,7);
    for(var i=0;i<30;i++){steady.Register(i);steady.Opening(i);}steady.Seal();
    for(var i=0;i<12;i++){steady.Bind(i,"vacancy-"+i);steady.Created("vacancy-"+i,0);}
    steady.Died("vacancy-0");Check(steady.TakeNext(1) is null,"a vacancy has a short refill delay");
    var ticket=steady.TakeNext(5);Check(ticket.HasValue&&steady.Alive==11,"one casualty starts refill without waiting for a nearly empty map");
    Check(steady.TakeNext(5.01) is null,"one casualty cannot release a batch");
}
{
    var regional = new WaveBudget(3,2,0,1,true,2,0,10);
    for (var i=0;i<5;i++) regional.Register(i);
    regional.Opening(0);
    regional.Bind(0,"opening");
    regional.TryActivate(0,0);
    regional.Created("opening",0);
    regional.Seal();
    double Priority(int first,int candidate) => first%2==candidate%2 ? 5-candidate : double.PositiveInfinity;
    Check(regional.TakeNext(0,Priority) is null && regional.TakeNext(.2,Priority)==3,
        "regional admission chooses a nearer ticket of the original role");
    Check(regional.TakeNext(.3,Priority) is null && regional.TakeNext(1.3,Priority)==1,
        "a skipped ticket remains available and the second admission obeys pacing");
    Check(regional.TakeNext(20,Priority) is null && regional.Alive+regional.Pending==3,
        "regional priority cannot bypass the simultaneous cap");
    regional.Bind(3,"regional");
    regional.TryActivate(3,20);
    regional.Created("regional",20);
    regional.Died("opening");
    Check(regional.TakeNext(20,Priority) is null && regional.TakeNext(21,Priority)==4,
        "deaths free exactly one slot and ranking cannot reissue an admitted ticket");
}
{
    var covered=new WaveBudget(4,2,0,1,true,2,0,7);
    for(var i=0;i<6;i++) covered.Register(i);
    covered.Opening(0);
    covered.Bind(0,"cover-0");
    covered.Created("cover-0",0);
    covered.Seal();
    covered.TakeNext(0);
    Check(covered.TakeNext(.1)==1&&covered.ReturnUnbound(1)&&covered.Pending==0,
        "an unsafe unbound reservation returns to the finite pool");
    bool DifferentZone(int ticket) => ticket!=1&&ticket!=2;
    covered.TakeNext(.2,eligible:DifferentZone);
    Check(covered.TakeNext(1.1,eligible:DifferentZone)==3,
        "an unsafe zone cannot block the next safe authored ticket");
    Check(covered.Bind(3,"cover-3")&&!covered.ReturnUnbound(3)&&!covered.ReturnUnbound(0),
        "bound and opening actors cannot be requeued");
    covered.Created("cover-3",1.1);
    covered.TakeNext(2.2);
    Check(covered.TakeNext(2.3)==1&&covered.Bind(1,"cover-1")&&covered.Created("cover-1",2.3),
        "the held identity can be admitted and counted once when its zone becomes safe");
    Check(covered.Total==6&&covered.Alive==3&&covered.Pending==0&&!covered.ReturnUnbound(1),
        "a returned reservation never changes population, duplicates a profile or exceeds the cap");
}
{
    var factory = new WaveBudget(20, 5, 35, 6, steady: true, pendingLimit: 2, jitter: 0, groupSize: 6);
    for (var ticket = 0; ticket < 56; ticket++)
    {
        factory.Register(ticket);
        if (ticket >= 20) continue;
        factory.Opening(ticket);
        factory.Bind(ticket, $"factory-{ticket}");
        factory.Created($"factory-{ticket}", 0);
    }
    factory.Seal();
    for (var ticket = 0; ticket < 14; ticket++) factory.Died($"factory-{ticket}");
    Check(factory.TakeNext(100) is null, "Factory holds its reserve while six opening actors remain");
    factory.Died("factory-14");

    var now = 101d;
    for (var group = 0; group < 6; group++)
    {
        Check(factory.TakeNext(now) is null && factory.TakeNext(now + 34.99) is null,
            "Factory opens a full looting window between reinforcement groups");
        now += 35;
        for (var member = 0; member < 6; member++)
        {
            var ticket = 20 + group * 6 + member;
            var id = $"factory-{ticket}";
            if (member > 0)
                Check(factory.TakeNext(now - .01) is null, "Factory spaces requests within each group");
            Check(factory.TakeNext(now) == ticket && factory.Bind(ticket, id), "Factory admits one finite reinforcement");
            Check(factory.TryActivate(ticket, now) && factory.Created(id, now), "Factory paces native births within each group");
            Check(factory.Alive + factory.Pending <= factory.Cap && factory.Activating == 0, "Factory maintains its simultaneous cap");
            factory.Died(id);
            now += 6;
        }
        Check(factory.Batch == group + 2, "Factory stops each group after six requests");
    }
    Check(factory.Waiting == 0 && factory.Spawned == 56, "Factory exhausts its fixed reserve");
    for (var ticket = 15; ticket < 20; ticket++) factory.Died($"factory-{ticket}");
    Check(factory.Cleared && factory.TakeNext(now + 100) is null, "Factory stays clear once the fixed population is dead");
}
// Retain legacy-mode regression coverage for compatibility and the fixed-pool invariants.
foreach(var total in new[]{30,60,90,110,140,180,230})
{
    var wave=new WaveBudget(24,2,8,.35);
    for(var i=0;i<total;i++){Check(wave.Register(i),"wave ticket registered");wave.Opening(i);}
    wave.Seal();var nextProfile=0;var live=new Queue<string>();
    void Arrive(int id){var p="wave-"+id;Check(wave.Bind(id,p)&&wave.Bind(id,p),"native spawn retry keeps same reservation");Check(wave.Created(p)&&!wave.Created(p),"spawn callback counted once");live.Enqueue(p);nextProfile++;}
    for(var i=0;i<Math.Min(24,total);i++)Arrive(i);
    for(var tick=0;tick<50000&&wave.Spawned<total;tick++)
    {
        if(live.Count>2){var id=live.Dequeue();Check(wave.Died(id)&&!wave.Died(id),"duplicate death does not add a replacement");}
        var ticket=wave.TakeNext(tick*.2);if(ticket.HasValue)Arrive(ticket.Value);
        Check(wave.Alive+wave.Pending<=wave.Cap,"finite wave peak includes profiles still loading");
    }
    Check(wave.Spawned==total&&wave.Issued==total&&wave.Waiting==0,"every fixed budget exhausted exactly once");
    while(live.Count>0)wave.Died(live.Dequeue());Check(wave.Cleared,"last infected really clears fixed pool");
    for(var i=0;i<50;i++)Check(wave.TakeNext(100000+i) is null,"no infinite reinforcements after total exhausted");
}
{
    var wave=new WaveBudget(4,1,8,.35);for(var i=0;i<10;i++){wave.Register(i);wave.Opening(i);}wave.Seal();
    Check(wave.TakeNext(1) is null&&wave.TakeNext(100) is null,"pending initial profiles cannot trigger a premature batch");
    for(var i=0;i<4;i++){wave.Bind(i,"pending-"+i);wave.Created("pending-"+i);}
    for(var i=0;i<3;i++)wave.Died("pending-"+i);
    Check(wave.TakeNext(101) is null&&wave.TakeNext(108.9) is null,"next wave has the configured breather");
    Check(wave.TakeNext(109).HasValue,"few survivors release next batch after breather");
    Check(wave.TakeNext(109.01) is null,"batch actors are staggered rather than all instantiated in one frame");
    wave.End();Check(wave.TakeNext(1000) is null&&!wave.Register(500)&&!wave.Created("pending-3"),"raid teardown blocks late callbacks and releases");
    var early=new WaveBudget(2,0,0,.1);early.Register(1);early.Opening(1);early.Bind(1,"early");early.Died("early");early.Created("early");early.Seal();Check(early.Cleared&&early.Alive==0,"death before creation callback is not resurrected in counters");
    Check(!early.Bind(1,"different")&&!early.Created("missing"),"unknown or replaced profiles cannot consume another spawn budget");
    var missed=new WaveBudget(2,0,0,.1);missed.Register(1);missed.Opening(1);missed.Bind(1,"bound");missed.Seal();
    Check(missed.IsBound("bound")&&!missed.IsBound("unrelated"),"reconciliation only accepts pre-bound native profiles");
    Check(missed.Pending==1&&missed.Created("bound")&&missed.Pending==0&&!missed.Created("bound"),"a missed native callback reconciles exactly once");
}

// Each native cache request stays bounded while preserving every fixed-raid role.
foreach(var total in new[]{30,60,90,110,140,180,230})
{
    var planned=new[]{new PreloadGroup("infectedAssault","normal",total-15),new PreloadGroup("infectedPmc","hard",15)};
    var batches=LoadingPolicy.Batches(planned,100);
    Check(batches.All(b=>b.Sum(p=>p.Count)<=100&&b.Sum(p=>p.Count)>0),"native profile request ceiling: "+total);
    Check(batches.SelectMany(b=>b).Sum(p=>p.Count)==total,"every fixed-raid profile preloaded exactly once: "+total);
    foreach(var group in planned)Check(batches.SelectMany(b=>b).Where(p=>p.Role==group.Role&&p.Difficulty==group.Difficulty).Sum(p=>p.Count)==group.Count,"full-raid role demand retained: "+total);
}
// Preloading is only a cache projection of the opening tickets, including all requested roles.
foreach(var map in mapSettings.AsObject())
{
    var plan=map.Value!["spawns"]!.AsArray().Select(w=>new PreloadGroup(w!["BossName"]!.ToString(),w["BossDifficult"]!.ToString(),1)).ToArray();
    var cap=WaveLimits.DefaultCap(map.Key);var first=LoadingPolicy.Opening(plan,cap)!;
    Check(first.Sum(p=>p.Count)==Math.Min(plan.Length,cap),"only the opening budget is preloaded: "+map.Key);
    foreach(var role in plan.Take(cap).GroupBy(p=>new{p.Role,p.Difficulty}))
        Check(first.Single(p=>p.Role==role.Key.Role&&p.Difficulty==role.Key.Difficulty).Count==role.Count(),"opening role and difficulty demand preserved: "+map.Key);
    Check(plan.Length==(int)map.Value["total"]!,"cache projection does not consume the full spawn plan: "+map.Key);
}
Check(LoadingPolicy.Opening(new[]{new PreloadGroup("assault","normal",10)},4) is null,"ordinary or mixed raids retain native preload behavior");
Check(LoadingPolicy.Opening(new[]{new PreloadGroup("infectedAssault","normal",-1)},4) is null,"malformed cache requests retain native behavior");
var nativeZero=LoadingPolicy.Opening(new[]{new PreloadGroup("infectedAssault","normal",1),new PreloadGroup("infectedAssault","normal",0),new PreloadGroup("infectedPmc","hard",1),new PreloadGroup("infectedAssault","normal",0)},2)!;
Check(nativeZero.Sum(p=>p.Count)==2&&nativeZero.Count==2,"native zero-escort entries do not disable or inflate opening preload");
var cut=LoadingPolicy.Opening(new[]{new PreloadGroup("infectedAssault","normal",10),new PreloadGroup("infectedPmc","hard",10)},12)!;
Check(cut.Sum(p=>p.Count)==12&&cut[1].Count==2,"cache cap handles grouped entries without overloading the opening");

{
    var collectionSourceRules=JsonNode.Parse(File.ReadAllText(Path.Combine(args[0],"content/server/data/quest-loot.json")))!.AsObject();
    var crate=(MongoId)collectionSourceRules["containers"]![0]!.ToString();
    var axe=(MongoId)collectionSourceRules["items"]![0]!["tpl"]!.ToString();
    var knife=(MongoId)collectionSourceRules["items"]![1]!["tpl"]!.ToString();
    Dictionary<MongoId,StaticLootDetails> Pools()=>new(){[crate]=new(){
        ItemCountDistribution=[new(){Count=2,RelativeProbability=1}],
        ItemDistribution=[new(){Tpl=tid,RelativeProbability=100000}]
    }};
    string CollectionPoolSnapshot(Dictionary<MongoId,StaticLootDetails> pools)=>JsonSerializer.Serialize(pools.Select(p=>new{
        Container=p.Key.ToString(), Counts=p.Value.ItemCountDistribution!.Select(c=>new{c.Count,c.RelativeProbability}),
        Items=p.Value.ItemDistribution!.Select(i=>new{Template=i.Tpl.ToString(),i.RelativeProbability})
    }));
    Check(QuestLootPolicy.Apply(null,collectionSourceRules,6).Count==0,"absent native lazy loot tables remain absent without a transform failure");
    var low=Pools();
    Check(QuestLootPolicy.Apply(low,collectionSourceRules,3).Count==0&&low[crate].ItemDistribution!.Count()==1,"rare quest tools do not enter lower danger maps");
    var factory=Pools();QuestLootPolicy.Apply(factory,collectionSourceRules,4);
    Check(factory[crate].ItemDistribution!.Any(x=>x.Tpl==axe&&x.RelativeProbability>0)&&!factory[crate].ItemDistribution!.Any(x=>x.Tpl==knife),"antique axe has a Factory-tier source; cultist blade remains higher-tier");
    var high=Pools();var original=high[crate].ItemDistribution!.First();
    Check(QuestLootPolicy.Apply(high,collectionSourceRules,5).Count==2,"both missing quest items gain high-danger weighted sources");
    Check(ReferenceEquals(high[crate].ItemDistribution!.First(),original)&&original.RelativeProbability==100000,"all pre-existing item weights and entries remain untouched");
    var once=CollectionPoolSnapshot(high);
    Check(QuestLootPolicy.Apply(high,collectionSourceRules,5).Count==0&&CollectionPoolSnapshot(high)==once,"reapplying collection sources never duplicates or inflates them");
    Check(QuestLootPolicy.Apply(new(),collectionSourceRules,6).Count==0,"source repair never invents a container or location");
    var empty=Pools();empty[crate].ItemCountDistribution=[new(){Count=0,RelativeProbability=1}];
    Check(QuestLootPolicy.Apply(empty,collectionSourceRules,6).Count==0,"empty native container pools are not activated");
    var existing=Pools();existing[crate].ItemDistribution=existing[crate].ItemDistribution!.Append(new(){Tpl=axe,RelativeProbability=7}).ToArray();
    QuestLootPolicy.Apply(existing,collectionSourceRules,5);
    Check(existing[crate].ItemDistribution!.Count(x=>x.Tpl==axe)==1&&existing[crate].ItemDistribution!.Single(x=>x.Tpl==axe).RelativeProbability==7,"existing positive third-party quest-item source retains its exact weight");
    var invalid=collectionSourceRules.DeepClone().AsObject();invalid["items"]![1]!["weightFraction"]=-1;
    var unchanged=Pools();var before=CollectionPoolSnapshot(unchanged);var rejected=false;
    try{QuestLootPolicy.Apply(unchanged,invalid,6);}catch(InvalidDataException){rejected=true;}
    Check(rejected&&CollectionPoolSnapshot(unchanged)==before,"malformed source specifications fail before mutating any pool");
    var nativeLazy=new SPTarkov.Server.Core.Utils.Json.LazyLoad<Dictionary<MongoId,StaticLootDetails>>(()=>Pools());
    QuestLootPolicy.Apply(nativeLazy.Value!,collectionSourceRules,5);
    Check(nativeLazy.Value![crate].ItemDistribution!.Count()==1,"native lazy loot re-read discards a one-off Value mutation");
    nativeLazy.AddTransformer(tables=>{QuestLootPolicy.Apply(tables!,collectionSourceRules,5);return tables;});
    Check(nativeLazy.Value![crate].ItemDistribution!.Count()==3,"registered collection transform survives the native lazy loader");
    Check(nativeLazy.Value![crate].ItemDistribution!.Count()==3,"re-reading native lazy loot retains exactly one copy of each added source");
}
var report = new { passed = true, count = checks.Count, checks, centreGun200 = gun, centreGunMaximum = Gun(10000), centreSuppressedMaximum = quiet, clientGameUiOrInRaidAcceptance = false };
Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {checks.Count} gameplay rule checks; centre gun {gun:F1}m, maximum 60m; suppressed maximum 15m.");
