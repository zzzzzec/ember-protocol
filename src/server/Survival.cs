using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Match = System.Text.RegularExpressions.Match;
using Path = System.IO.Path;
using System.Text.Json.Nodes;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Bot;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Eft.Repair;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Codex.ZombieSurvival;

public sealed record Metadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "codex.zombie-survival";
    public override string Name { get; init; } = "SPT-Z Survival";
    public override string Author { get; init; } = "Ember Protocol contributors";
    public override List<string> Contributors { get; init; } = [];
    public override SemanticVersioning.Version Version { get; init; } = new("2.21.6");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.13");
    public override List<string> Incompatibilities { get; init; } = ["com.vonbraunz.roamingzombies"];
    public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = [];
    public override string Url { get; init; } = "";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "Private local mod";
}

[Injectable(InjectionType.Singleton)]
public sealed partial class SurvivalService(JsonUtil json, DatabaseService db, ConfigServer configs,
    SaveServer saves, ImageRouter images, ProfileActivityService activity,
    EventOutputHolder outputs, HttpResponseUtil responses, TraderHelper traderHelper, HandbookHelper handbookHelper, RagfairPriceService ragfairPrices, FenceService fenceService, ISptLogger<SurvivalService> log, IServiceScopeFactory scopes)
{
    public static SurvivalService Current { get; private set; } = null!;
    public string Folder { get; } = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    public JsonObject Config { get; private set; } = null!;
    public string TraderId => Config["traderId"]!.GetValue<string>();
    public JsonObject Maps => Config["maps"]!.AsObject();
    private readonly ConcurrentDictionary<string,string> _sessionMaps = new();
    private Dictionary<string,JsonObject> _items = [];
    private JsonObject _authoredLocales = new();
    private HashSet<MongoId> _authoredQuestIds = [];
    private readonly List<(MongoId Quest,MongoId Recipe)> _recipeGates=[];
    public void ReportStartupFailure(string stage,Exception error)
    {
        var file=Path.Combine(Folder,"startup-errors.jsonl");
        var message=Redact(error.ToString());
        try{File.AppendAllText(file,new JsonObject{["utc"]=DateTimeOffset.UtcNow.ToString("O"),["stage"]=stage,["version"]=Config?["version"]?.DeepClone(),["error"]=message}.ToJsonString()+Environment.NewLine);}
        catch(Exception writeError){log.Error("[SPT-Z] Cannot write startup error: "+writeError.Message,null);}
        log.Error("[SPT-Z] Startup failed in "+stage+". Details: "+file+Environment.NewLine+message,null);
    }
    public JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder,path)))!;
    private T Parse<T>(JsonNode n) => json.Deserialize<T>(n.ToJsonString())!;
    private JsonNode Node<T>(T x) => JsonNode.Parse(json.Serialize(x))!;
    private T Edit<T>(T x, Action<JsonNode> edit) { var n=Node(x);edit(n);return Parse<T>(n); }
    private void EditInPlace<T>(T x, Action<JsonNode> edit) where T:class
    {
        var replacement=Edit(x,edit);
        foreach(var p in typeof(T).GetProperties().Where(p=>p.CanRead&&p.CanWrite&&p.GetIndexParameters().Length==0)) p.SetValue(x,p.GetValue(replacement));
    }

    public void Register()
    {
        Current=this;Config=Read("config.json").AsObject();
        var runtimePath=Path.Combine(Folder,"runtime.json");
        Config["instanceKind"]=File.Exists(runtimePath)?JsonNode.Parse(File.ReadAllText(runtimePath))?["instanceKind"]?.DeepClone():JsonValue.Create("Player");
        LoadCampaign();
        try{InitializeDiagnostics();}catch(Exception e){log.Warning("[SPT-Z] Diagnostics unavailable: "+e.Message,null);}
        foreach(var r in Read("data/recipes.json").AsArray())
            foreach(var req in r!["requirements"]!.AsArray())
                if(req?["type"]?.ToString()=="QuestComplete")_recipeGates.Add(((MongoId)req["questId"]!.ToString(),(MongoId)r["_id"]!.ToString()));
        var trader=json.DeserializeFromFile<Trader>(Path.Combine(Folder,"data/trader.json"))!;
        if(!db.GetTraders().TryAdd((MongoId)TraderId,trader)) throw new InvalidDataException("SPT-Z trader id collision");
        configs.GetConfig<TraderConfig>().UpdateTime.Add(new UpdateTime { TraderId=(MongoId)TraderId,Seconds=new MinMax<int>((int)Config["restockSeconds"]!, (int)Config["restockSeconds"]!)});
        configs.GetConfig<RagfairConfig>().Traders.Clear();
        images.AddRoute("/files/trader/avatar/z_survival",Path.Combine(Folder,"data/avatar.png"));
        images.AddRoute("/files/trader/avatar/ember_quartermaster",Path.Combine(Folder,"data/avatar-ember.png"));
        RegisterOpeningTrader();
        RegisterQuestIllustrations();
        InitializeVanillaGiftPolicy();
        var loc=Read("data/locales.json").AsObject();_authoredLocales=loc;
        InitializeAbundanceAwards(loc);
        foreach(var (lang,lazy) in db.GetLocales().Global)
        {
            var entries=(loc[lang]??loc["en"])!.AsObject().ToDictionary(x=>x.Key,x=>x.Value!.GetValue<string>());
            lazy.AddTransformer(existing=>{foreach(var pair in entries)existing[pair.Key]=pair.Value;return existing;});
        }
        var profileData=Read("data/profiles.json");
        var templates=db.GetProfileTemplates();
        foreach(var key in templates.Keys.ToList())
            if(profileData[key] is { } p) templates[key]=ParseSame(p,templates[key]);
        log.Success("[SPT-Z] Quartermaster registered; sparse PM starting kit configured for new accounts.",null);
    }
    private T ParseSame<T>(JsonNode n,T _) => Parse<T>(n);

    public void Apply()
    {
        db.GetQuests().Clear();
        var own=json.DeserializeFromFile<Dictionary<MongoId,Quest>>(Path.Combine(Folder,"data/quests.json"))!;
        SetQuestMapLabels(own.Values);
        _authoredQuestIds=own.Keys.ToHashSet();
        var storyOrder=_campaign["main"]!.AsArray().Select((row,index)=>(Id:row!["id"]!.ToString(),Index:index)).ToDictionary(x=>x.Id,x=>x.Index);
        foreach(var q in own.OrderBy(q=>storyOrder.GetValueOrDefault(q.Key.ToString(),int.MaxValue))) db.GetQuests()[q.Key]=q.Value;
        EditInPlace(configs.GetConfig<QuestConfig>(),n=>{foreach(var q in n["repeatableQuests"]?.AsArray()??[]){q!["numQuests"]=0;q["minPlayerLevel"]=999;}});
        // SPT also patches the client's constructor through this separate endpoint.
        // Align it with the location and the generated client settings.
        if((bool?)Config["waves"]?["enabled"]==true)EditInPlace(configs.GetConfig<BotConfig>(),n=>{
            var caps=n["maxBotCap"]!.AsObject();
            foreach(var (map,spec) in Maps)caps[map]=WaveCap(map,(int)spec!["total"]!);
        });
        if((bool?)Config["loading"]?["enabled"]==true)EditInPlace(configs.GetConfig<BotConfig>(),n=>{
            var batch=n["presetBatch"]!.AsObject();
            foreach(var role in new[]{"infectedAssault","infectedPmc","infectedCivil","infectedLaborant","infectedTagilla"})batch[role]=Math.Clamp((int?)Config["loading"]?["profileBatchSize"]??6,1,30);
        });
        db.GetBots().Core.ActiveHalloweenZombiesEvent=true;
        var infection=db.GetGlobals().Configuration.SeasonActivity.InfectionHalloween;
        infection.Enabled=true;infection.DisplayUIEnabled=true;
        EditInPlace(db.GetGlobals().Configuration,n=>{
            n["RagFair"]!["minUserLevel"]=999;
            var progression=Config["skillProgression"]!;
            n["SkillsSettings"]!["SkillProgressRate"]=progression["skillRate"]!.DeepClone();
            n["SkillsSettings"]!["WeaponSkillProgressRate"]=progression["weaponRate"]!.DeepClone();
            n["SkillFreshPoints"]=progression["freshPoints"]!.DeepClone();
            n["SkillPointsBeforeFatigue"]=progression["pointsBeforeFatigue"]!.DeepClone();
            n["SkillFatiguePerPoint"]=progression["fatiguePerPoint"]!.DeepClone();
            n["SkillMinEffectiveness"]=progression["minimumEffectiveness"]!.DeepClone();
            n["SkillFatigueReset"]=progression["fatigueResetSeconds"]!.DeepClone();
            var skills=n["SkillsSettings"]!;
            skills["Metabolism"]!["EnergyRecoveryRate"]=progression["metabolismRecoveryRate"]!.DeepClone();
            skills["Metabolism"]!["HydrationRecoveryRate"]=progression["metabolismRecoveryRate"]!.DeepClone();
            skills["Health"]!["SkillProgress"]=progression["healthSkillProgress"]!.DeepClone();
            skills["MagDrills"]!["RaidLoadedAmmoAction"]=progression["magDrillsLoadAction"]!.DeepClone();
            var search = skills["Search"]!;
            var searchGain = progression["searchGainMultiplier"]!.GetValue<double>();
            foreach (var action in new[] { "SearchAction", "FindAction" })
                search[action] = search[action]!.GetValue<double>() * searchGain;
            // This mode supplies infected spawns independently of the legacy summoning event.
            if((bool?)Config["disableLegacyRitual"]==true)n["EventSettings"]!["EventActive"]=false;
            // Native melee bleeding applies this named buff afterward. Removing
            // the bundle disables infection without changing the bleeding rolls.
            n["Health"]!["Effects"]!["Stimulator"]!["Buffs"]!.AsObject().Remove("BuffsZombieInfection");
        });
        foreach(var (key,location) in db.GetLocations().GetDictionary())
        {
            if(location?.Base is null)continue;
            var map=Node(location.Base)["Id"]?.ToString().ToLowerInvariant()??key.ToLowerInvariant();
            if(map is "hideout" or "private area" or "privatearea")continue;
            location.Base=ConfigureLocation(location.Base,map);
        }
        ConfigureQuestLootSources();
        var ak50 = Read("data/ak50.json").AsObject();
        ConfigureAk50Handguard(ak50);
        _ak50ReceiverRecipe = (MongoId)ak50["recipe"]!.ToString();
        var mapLootMultiplier=(double)Config["mapLootMultiplier"]!;
        var locationConfig=configs.GetConfig<LocationConfig>();
        var mapLoot=new JsonObject();
        foreach(var (map,_) in Maps)
        {
            var loose=locationConfig.LooseLootMultiplier[map]*mapLootMultiplier;
            var containers=locationConfig.StaticLootMultiplier[map]*mapLootMultiplier;
            locationConfig.LooseLootMultiplier[map]=loose;
            locationConfig.StaticLootMultiplier[map]=containers;
            mapLoot[map]=new JsonObject{["loose"]=loose,["containers"]=containers};
        }
        foreach(var key in db.GetGlobals().LocationInfection.Keys.ToArray())db.GetGlobals().LocationInfection[key]=0;
        foreach(var key in new[]{"Sandbox","bigmap","Interchange","factory4","RezervBase","laboratory"})db.GetGlobals().LocationInfection[key]=100;
        foreach(var (id,trader) in db.GetTraders())
        {
            if(ActiveTrader(id))continue;
            trader.Base.UnlockedByDefault=false;
            trader.Base.Repair.Availability=false;
            trader.Base.Insurance.Availability=false;
        }
        // Map all hideout trader prerequisites onto the sole remaining trader.
        // Preserve SVM hideout counts, durations and other settings.
        var milestone=Read("data/manifest.json")["quests"]!.AsArray().First(q=>q?["lane"]?.ToString()=="sidearm"&&(int?)q["index"]==2)!["id"]!.ToString();
        void ReplaceRequirements(JsonNode? node)
        {
            if(node is JsonObject o)
            {
                if(o["type"]?.ToString()=="TraderLoyalty")o["traderId"]=TraderId;
                if(o["type"]?.ToString()=="QuestComplete")o["questId"]=milestone;
                foreach(var pair in o.ToArray())ReplaceRequirements(pair.Value);
            }
            else if(node is JsonArray array)foreach(var value in array)ReplaceRequirements(value);
        }
        EditInPlace(db.GetHideout(),ReplaceRequirements);
        InitializeClosedMapKeys();
        InitializeRaidRestock();
        foreach(var (id,profile) in saves.GetProfiles()) EnsureProfile(id);
        _items=db.GetItems().ToDictionary(x=>x.Key.ToString(),x=>Node(x.Value).AsObject());
        ConfigureRareKeyLoot();
        InitializeAbundanceLoot();
        InitializeHelmetLoot();
        InitializeThreatGenerator();
        var baselineRecipeCaps=Read("data/camp-growth.json")["nativeRecipeBatchCaps"]!.AsObject();
        // Ammo production remains useful without restoring bulk ammunition supply.
        EditInPlace(db.GetHideout(),n=>{
            foreach(var recipe in n["production"]?["recipes"]?.AsArray()??[])
            {
                var product=recipe?["endProduct"]?.ToString();
                if(product is null)continue;
                var category=Category(product);
                if(category is "medical" or "advancedMedical" or "drug" or "stim" or "food" or "drink")
                    recipe!["count"]=Math.Min((int?)recipe["count"]??1,category is "advancedMedical" or "stim"?1:2);
                // Explicit base-game caps keep quest-unlocked batches meaningful.
                if(baselineRecipeCaps[recipe!["_id"]!.ToString()] is { } cap)
                    recipe["count"]=Math.Min((int?)recipe["count"]??1,(int)cap);
                if(category!="ammo")continue;
                var calibre=_items[product]["_props"]?["Caliber"]?.ToString()??"";
                var large=calibre.Contains("762x51")||calibre.Contains("762x54")||calibre.Contains("9x39")||calibre.Contains("86x70")||calibre.Contains("127x");
                var rifle=calibre.Contains("545x39")||calibre.Contains("556x45")||calibre.Contains("762x39");
                recipe!["count"]=Math.Min((int?)recipe["count"]??1,large?4:rifle?12:24);
            }
        });
        // Add after native recipe caps and after SVM, preserving deliberate batch sizes.
        var customRecipeTimeMultiplier = ReadSvmRecipeTimeMultiplier();
        EditInPlace(db.GetHideout(),n=>{
            var recipes=n["production"]!["recipes"]!.AsArray();
            var added=Read("data/recipes.json").AsArray();
            var ids=added.Select(r=>r!["_id"]!.ToString()).ToHashSet();
            ids.Add(ak50["nativeRecipe"]!.ToString());
            for(var i=recipes.Count-1;i>=0;i--)if(ids.Contains(recipes[i]?["_id"]?.ToString()??""))recipes.RemoveAt(i);
            foreach (var recipe in added)
            {
                if (customRecipeTimeMultiplier is { } multiplier &&
                    (bool?)recipe!["continuous"] == false &&
                    (double)recipe["productionTime"]! >= 10)
                {
                    var seconds = (double)recipe["productionTime"]! * multiplier;
                    recipe["productionTime"] = seconds < 1 ? 2 : seconds;
                }
                recipes.Add(recipe!.DeepClone());
            }
        });
        ApplyEconomy();
        ConfigureHideoutRewardPools();
        InitializeLabBlackout();
        ConfigureFence(ak50["weapon"]!.ToString());
        ConfigureSupplyRoutes();
        ConfigureRoubleBuying();
        InstallPatches();
        if(FenceEnabled)fenceService.GenerateFenceAssorts();
        var report=new JsonObject { ["version"]=Config["version"]?.DeepClone(),["traderId"]=TraderId,["quests"]=own.Count,["customRecipes"]=Read("data/recipes.json").AsArray().Count,["customRecipeTimeMultiplier"]=customRecipeTimeMultiplier,["fixedRaidPopulation"]=true,["factoryPopulationVariesBetweenRaids"]=true,["finiteWaves"]=(bool?)Config["waves"]?["enabled"]==true,["infiniteReinforcements"]=false,["maps"]=Maps.DeepClone(),["mapLootMultipliers"]=mapLoot,["infectedCorpseSearchable"]=false,["campaign"]=_campaign.DeepClone(),["generatedUtc"]=DateTimeOffset.UtcNow.ToString("O")};
        File.WriteAllText(Path.Combine(Folder,"startup-report.json"),report.ToJsonString(new(){WriteIndented=true}));
        log.Success($"[SPT-Z] {own.Count} survival contracts; Ember Protocol campaign; six map tiers; finite waves with per-raid budgets; infected corpses sealed.",null);
    }

    private double? ReadSvmRecipeTimeMultiplier()
    {
        var svm = Path.Combine(Path.GetDirectoryName(Folder)!, "[SVM] Server Value Modifier");
        if (!File.Exists(Path.Combine(svm, "ServerValueModifier.dll"))) return null;

        try
        {
            var loader = JsonNode.Parse(File.ReadAllText(Path.Combine(svm, "Loader", "loader.json")));
            var preset = (string?)loader?["CurrentlySelectedPreset"];
            if (string.IsNullOrWhiteSpace(preset) || preset.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (preset.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || preset is "." or "..")
                throw new InvalidDataException("Invalid preset name.");
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(svm, "Presets", preset + ".json")));
            var hideout = settings?["Hideout"];
            if ((bool?)hideout?["EnableHideout"] != true) return null;

            var multiplier = (double?)hideout["HideoutProdMult"] ?? 1;
            if (!double.IsFinite(multiplier) || multiplier < 0)
                throw new InvalidDataException("Invalid crafting multiplier.");
            return multiplier;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            log.Warning("[SPT-Z] Could not read the active SVM crafting multiplier: " + error.Message, null);
            return null;
        }
    }

    public int WaveCap(string map,int total)
    {
        if((bool?)Config["waves"]?["enabled"]!=true)return total;
        var cap=Codex.ZombieSurvival.Rules.WaveLimits.Cap(map,(int?)Config["waves"]?["aliveCaps"]?[map]??0,total);
        return Codex.ZombieSurvival.Rules.WaveLimits.IsFactory(map)
            ? Codex.ZombieSurvival.Rules.WaveLimits.FactoryActiveCap(cap,total,(int)Maps[map]!["total"]!)
            : cap;
    }

    public LocationBase ConfigureLocation(LocationBase original,string map,MongoId? session=null)
    {
        return Edit(original,n=>{
            var active=session.HasValue?MapSpecFor(session.Value,map):Maps[map] as JsonObject;
            n["Enabled"]=active is not null;n["Locked"]=active is null;
            // Apply before returning locked maps and again to each native raid response.
            // Never enable a transit that vanilla, SVM or an event has already closed.
            if(n["transits"] is JsonArray transits)
                foreach(var transit in transits.OfType<JsonObject>())
                    if(active is null||!Maps.ContainsKey(transit["location"]?.ToString().ToLowerInvariant()??""))
                        transit["active"]=false;
            n["waves"]=new JsonArray();n["BossLocationSpawn"]=active?["spawns"]?.DeepClone()??new JsonArray();
            if(active is null)return;
            // The native raid response already uses this database location.
            if(!session.HasValue && Config["naturalAirdropChanceMultiplier"] is { } multiplier
                && n["AirdropParameters"] is JsonArray airdrops)
                foreach(var airdrop in airdrops.OfType<JsonObject>())
                    if(airdrop["PlaneAirdropChance"] is { } chance)
                        airdrop["PlaneAirdropChance"]=(double)chance*(double)multiplier;
            var total=(int)active["total"]!;var cap=WaveCap(map,total);
            n["BotMax"]=cap;n["BotMaxPlayer"]=cap;
            if((bool?)Config["waves"]?["enabled"]==true){n["OldSpawn"]=true;n["NewSpawn"]=false;if(n["NonWaveGroupScenario"] is JsonObject nonWave)nonWave["Enabled"]=false;}
            if(n["Events"]?["Halloween2024"] is JsonObject h)
            {
                h["InfectionPercentage"]=100;h["CrowdsLimit"]=0;h["ZombieMultiplier"]=1;
                h["MaxCrowdAttackSpawnLimit"]=0;h["CrowdAttackSpawnParams"]=new JsonArray();
            }
        });
    }

    internal List<HideoutProduction>? MatchAuthoredCraftReward(Reward reward,MongoId quest)
    {
        var recipes=_recipeGates.Where(gate=>gate.Quest==quest).Select(gate=>gate.Recipe).ToHashSet();
        if(recipes.Count==0)return null;
        var product=reward.Items?.FirstOrDefault()?.Template;
        if(!product.HasValue||!int.TryParse(reward.TraderId?.ToString(),out var area))return [];
        // The native fallback does not restrict matches to the rewarding quest.
        var production=db.GetHideout().Production?.Recipes;
        if(production is null)return [];
        return production.Where(recipe=>recipes.Contains(recipe.Id)
            && (int?)recipe.AreaType==area && recipe.EndProduct==product.Value && recipe.Locked==true
            && recipe.Requirements?.Any(req=>req.RequiredLevel==reward.LoyaltyLevel)==true).ToList();
    }

    public void EnsureProfile(MongoId session)
    {
        if(!saves.GetProfiles().TryGetValue(session,out var p))return;
        NormalizeLegacyHealth(session,p);
        QuarantinePendingVanillaGifts(session);
        var pmc=p.CharacterData?.PmcData;if(pmc?.TradersInfo is null)return;
        foreach(var (id,t) in pmc.TradersInfo){if(IsOpeningTrader(id.ToString()))continue;t.Disabled=!ActiveTrader(id);t.Unlocked=ActiveTrader(id);}
        pmc.TradersInfo.TryAdd((MongoId)TraderId,new TraderInfo {Standing=0,LoyaltyLevel=1,SalesSum=0,Unlocked=true,Disabled=false,NextResupply=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(int)Config["restockSeconds"]!});
        if(RaidRestockEnabled){pmc.TradersInfo[(MongoId)TraderId].NextResupply=RestockPolicy.RaidClock;db.GetTrader((MongoId)TraderId).Base.NextResupply=RestockPolicy.RaidClock;}
        traderHelper.LevelUp((MongoId)TraderId,pmc);
        EnsureFenceProfile(session,p);
        EnsureOpeningProfile(session,p);
        pmc.UnlockedInfo??=new UnlockedInfo();
        pmc.UnlockedInfo.UnlockedProductionRecipe??=[];
        foreach(var gate in _recipeGates)
            if(pmc.Quests?.Any(q=>q.QId==gate.Quest&&(int)q.Status==4)==true)pmc.UnlockedInfo.UnlockedProductionRecipe.Add(gate.Recipe);
    }
    public void Start(MongoId session,StartLocalRaidRequestData request)
    {
        var map=request.Location.ToLowerInvariant();
        if(!Maps.ContainsKey(map))throw new InvalidOperationException("这张地图未开放。");
        if(!string.Equals(request.PlayerSide,"Pmc",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("丧尸生存只开放 PMC 战局。");
        _sessionMaps[session.ToString()]=map;
        CaptureRaidSpec(session,map);
    }
    public string MapFor(MongoId session) => _sessionMaps.GetValueOrDefault(session.ToString())
        ??activity.GetProfileActivityRaidData(session)?.RaidConfiguration?.Location?.ToLowerInvariant()??"sandbox";
    public void ConstrainRequest(GenerateBotsRequestData request)
    {
        EditInPlace(request,n=>{foreach(var c in n["conditions"]?.AsArray()??[]){
            var role=c?["Role"]?.ToString()??c?["role"]?.ToString()??"";
            if(role.StartsWith("infected",StringComparison.OrdinalIgnoreCase))continue;
            if(c is JsonObject o){o[o.ContainsKey("Role")?"Role":"role"]="infectedAssault";o[o.ContainsKey("Difficulty")?"Difficulty":"difficulty"]="normal";}
        }});
    }
    public async Task<IEnumerable<BotBase?>> ScaleBots(Task<IEnumerable<BotBase?>> task,MongoId session)
    {
        var bots=(await task).ToList();var map=MapSpecFor(session,MapFor(session))??Maps["sandbox"]!.AsObject();
        var result=bots.Select(bot=>bot is null?null:Edit(bot,n=>{
            var role=n["Info"]?["Settings"]?["Role"]?.ToString()??"";
            if(!role.StartsWith("infected",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Non-infected generated: "+role);
            if(!ApplyThreatBot(n,role,session)&&n["Health"]?["BodyParts"] is JsonObject parts)
                foreach(var (part,body) in parts)
                {
                    var hp=part=="Head"?(int)map["head"]!:part=="Chest"?(int)map["chest"]!:part=="Stomach"?(int)((int)map["chest"]!*.7):60;
                    if(body?["Health"] is JsonObject health){health["Current"]=hp;health["Maximum"]=hp;}
                }
        })).ToList();
        CaptureDiagnostic(()=>Trace("bots.generated",session,new {map=MapFor(session),bots=result.Where(b=>b is not null).Select(b=>{var n=Node(b);return new {role=n["Info"]?["Settings"]?["Role"]?.ToString(),difficulty=n["Info"]?["Settings"]?["BotDifficulty"]?.ToString(),headHp=n["Health"]?["BodyParts"]?["Head"]?["Health"]?["Maximum"]?.DeepClone()};}).ToArray()}));
        return result;
    }
    public void RestoreAuthoredLocales(string language,Dictionary<string,string> values)
    {foreach(var (key,value) in (_authoredLocales[language]??_authoredLocales["en"])!.AsObject())values[key]=value!.GetValue<string>();}
    public ItemEventRouterResponse Denied(MongoId session) => responses.AppendErrorToOutput(outputs.GetOutput(session),"该交易渠道未开放。",BackendErrorCodes.RagfairUnavailable);

    public string Category(string tpl)
    {
        if(!_items.TryGetValue(tpl,out var item))return "other";
        var p=item["_props"];
        if((bool?)p?["IsStationaryWeapon"]==true)return "other";
        if(p?["ammoType"] is not null)return "ammo";
        if(tpl is "5d02778e86f774203e7dedbe" or "5d02797c86f774203f38e30a")return "advancedMedical";
        var visited=new HashSet<string>();var parent=tpl;
        while(_items.TryGetValue(parent,out var current)&&visited.Add(parent))
        {
            if(parent=="5422acb9af1c889c16000029")return p?["weapClass"]?.ToString()=="pistol"?"pistol":"longGun";
            if(parent=="543be5cb4bdc2deb348b4568")return "ammo";
            if(parent=="644120aa86ffbe10ee032b6f")return "plate";
            if(parent=="5448e8d04bdc2ddf718b4569")return "food";
            if(parent=="5448e8d64bdc2dce718b4568")return "drink";
            if(parent=="5448f3a64bdc2d60728b456a")return "stim";
            if(parent=="5448f3a14bdc2d27728b4569")return "drug";
            if(parent=="5448f39d4bdc2d0a728b4568")return (int?)p?["MaxHpResource"]>=600?"advancedMedical":"medical";
            if(parent=="5448f3ac4bdc2dce718b4569")return "medical";
            if(parent is "5448e54d4bdc2dcc718b4568" or "5448e5724bdc2ddf718b4568" || double.TryParse(p?["armorClass"]?.ToString(),out var armor)&&armor>0)return "armor";
            parent=current["_parent"]?.ToString()??"";
        }
        // Ammunition boxes must not bypass scarcity through their container template.
        if(p?["StackSlots"] is JsonArray slots && slots.ToJsonString().Contains("5485a8684bdc2da71d8b4567"))return "ammo";
        return "other";
    }
    // The bus-side Scav body (6582e6bb0c3b9823fe6d1840) has its own loot table.
    private const string MiraFlareCorpse = "container_Sandbox_Design_Stuff_00011";
    private const string GreenHandheldFlare = "6217726288ed9f0845317459";
    private const string TankBattery = "5d03794386f77420415576f5";
    private const double ReserveTankBatteryChance = .25;

    public JsonArray FilterItems(JsonArray list,double factor=1,Random? random=null,bool preserveMiraFlare=false,
        bool limitReserveBattery=false,JsonObject? keptReserveBattery=null)
    {
        random??=Random.Shared;var rules=Config["scarcity"]!;
        var ids=list.OfType<JsonObject>().ToDictionary(x=>x["_id"]!.ToString());var removed=new HashSet<string>();
        bool AncestorRemoved(JsonObject item){var seen=new HashSet<string>();var parent=item["parentId"]?.ToString();while(parent is not null&&seen.Add(parent)&&ids.TryGetValue(parent,out var p)){if(removed.Contains(parent))return true;parent=p["parentId"]?.ToString();}return false;}
        foreach(var item in list.OfType<JsonObject>())
        {
            var id=item["_id"]!.ToString();var tpl=item["_tpl"]!.ToString();
            if(limitReserveBattery && tpl==TankBattery && !ReferenceEquals(item,keptReserveBattery))
            { removed.Add(id); continue; }
            if (IsClosedMapKey(tpl)) { removed.Add(id); continue; }
            // EFT classifies this single-use signal cartridge as a weapon.
            if (preserveMiraFlare && tpl == GreenHandheldFlare) continue;
            var category=Category(tpl);
            if(category=="other")continue;
            var parent=item["parentId"]?.ToString();
            // Keep loaded weapons and fitted armor intact when filtering loose supplies.
            var ancestor=parent;var ancestors=new HashSet<string>();var attached=false;
            while(ancestor is not null&&ancestors.Add(ancestor)&&ids.TryGetValue(ancestor,out var enclosing))
            {
                var enclosingTpl=enclosing["_tpl"]!.ToString();
                if(_helmetInserts.ContainsKey(enclosingTpl) || Category(enclosingTpl) is "pistol" or "longGun" or "armor" || (bool?)_items.GetValueOrDefault(enclosingTpl)?["_props"]?["IsStationaryWeapon"]==true){attached=true;break;}
                ancestor=enclosing["parentId"]?.ToString();
            }
            if(attached)continue;
            if((bool?)rules["fullAmmoPacks"]==true&&parent is not null&&ids.TryGetValue(parent,out var sealedBox)
                &&AmmoPackPolicy.Describe(_items,sealedBox["_tpl"]!.ToString()) is not null)continue;
            var chance=category=="ammo"?1:(double)rules[category+"Keep"]!;
            var cap=category is "medical" or "advancedMedical" or "drug" or "stim" or "food" or "drink"?(int?)rules["consumableStack"]??1:0;
            if(category=="ammo")
            {
                var props=_items[tpl]["_props"]!;
                if(props["ammoType"] is null)
                    foreach(Match match in Regex.Matches(props["StackSlots"]?.ToJsonString()??"","[0-9a-f]{24}"))
                        if(_items.TryGetValue(match.Value,out var content)&&content["_props"]?["ammoType"] is not null){props=content["_props"]!;break;}
                var calibre=props["Caliber"]?.ToString()??"";
                var large=calibre.Contains("762x51")||calibre.Contains("762x54")||calibre.Contains("9x39")||calibre.Contains("86x70")||calibre.Contains("127x")||calibre.Contains("40x");
                var rifle=calibre.Contains("545x39")||calibre.Contains("556x45")||calibre.Contains("762x39");
                var prefix=large?"largeAmmo":rifle?"rifleAmmo":"smallAmmo";
                chance=(double)rules[prefix+"Keep"]!;cap=(int)rules[prefix+"Stack"]!;
                if((double?)props["PenetrationPower"]>=35)chance*=.45;
                if(parent is not null&&ids.TryGetValue(parent,out var box)&&Category(box["_tpl"]!.ToString())=="ammo")chance=1;
            }
            if(random.NextDouble()>Math.Min(1,chance*factor)){removed.Add(id);continue;}
            if(cap>0&&item["upd"] is JsonObject upd&&upd["StackObjectsCount"] is not null)upd["StackObjectsCount"]=Math.Max(1,Math.Min((int)decimal.Parse(upd["StackObjectsCount"]!.ToString(),System.Globalization.CultureInfo.InvariantCulture),cap));
        }
        var result=new JsonArray();foreach(var item in list.OfType<JsonObject>())if(!removed.Contains(item["_id"]!.ToString())&&!AncestorRemoved(item))result.Add(item.DeepClone());
        CompleteHelmetLoot(result);
        if((bool?)rules["fullAmmoPacks"]==true)AmmoPackPolicy.Fill(result,_items);
        foreach(var group in result.OfType<JsonObject>().Where(x=>x["slotId"]?.ToString()=="cartridges").GroupBy(x=>x["parentId"]?.ToString()))
        {var index=0;foreach(var cartridge in group.OrderBy(x=>(int?)x["location"]??0))cartridge["location"]=index++;}
        return result;
    }
    public List<SpawnpointTemplate> FilterLoot(List<SpawnpointTemplate> loot,string location)
    {
        var map = location.ToLowerInvariant();
        var data=Node(loot).AsArray();var factor=(int?)Maps[map]?["rank"]??1;
        var limitReserveBattery = map == "rezervbase";
        JsonObject? keptReserveBattery = null;
        // Roll once over the generated raid so additional containers and
        // abundance draws cannot turn several independent chances into a guarantee.
        if (limitReserveBattery && Random.Shared.NextDouble() < ReserveTankBatteryChance)
        {
            var candidates = data.OfType<JsonObject>().SelectMany(spawn => (spawn["Items"] as JsonArray ?? []).OfType<JsonObject>())
                .Where(item => item["_tpl"]?.ToString() == TankBattery).ToArray();
            if (candidates.Length > 0) keptReserveBattery = candidates[Random.Shared.Next(candidates.Length)];
        }
        var result=new JsonArray();
        foreach(var spawn in data)
        {
            if(spawn?["Items"] is JsonArray list)
            {
                var preserveMiraFlare = (map is "sandbox" or "sandbox_high") && spawn["Id"]?.ToString() == MiraFlareCorpse;
                var items = FilterItems(list,.80+factor*.08,preserveMiraFlare:preserveMiraFlare,
                    limitReserveBattery:limitReserveBattery,keptReserveBattery:keptReserveBattery);
                WearRareWorldKeys(items, map, spawn["Id"]?.ToString() ?? "");
                spawn["Items"] = items;
                var root=spawn["Root"]?.ToString();
                if(root is not null&&!spawn["Items"]!.AsArray().Any(x=>x?["_id"]?.ToString()==root))continue;
            }
            result.Add(spawn?.DeepClone());
        }
        return Parse<List<SpawnpointTemplate>>(result);
    }
    public List<List<Item>> FilterRewards(IEnumerable<List<Item>> rewards)=>rewards.Select(items=>Parse<List<Item>>(FilterItems(Node(items).AsArray()))).Where(items=>items.Count>0).ToList();
    private void Patch(Harmony harmony,Type type,string method,string? prefix=null,string? postfix=null)
    {
        var target=AccessTools.Method(type,method)??throw new MissingMethodException(type.FullName,method);
        harmony.Patch(target,prefix is null?null:new HarmonyMethod(typeof(Patches),prefix),postfix is null?null:new HarmonyMethod(typeof(Patches),postfix));
    }
    private void InstallPatches()
    {
        var h=new Harmony("codex.spt-z.rules");
        Patch(h,typeof(HideoutController),"HandlePresetReward",postfix:nameof(Patches.BareReceiverReward));
        InstallFieldQuestPatches(h);
        InstallStoryPatches(h);
        InstallAbundanceLoot(h);
        Patch(h,typeof(RepairController),"TraderRepair",prefix:nameof(Patches.TraderRepairAccess));
        Patch(h,typeof(RepairController),"RepairWithKit",prefix:nameof(Patches.KitRepairAccess));
        Patch(h,typeof(LocaleService),"GetLocaleDb",postfix:nameof(Patches.AuthoredLocales));
        Patch(h,typeof(GiftService),"SendGiftToPlayer",prefix:nameof(Patches.CatalogGift));
        Patch(h,typeof(MailSendService),"SendMessageToPlayer",prefix:nameof(Patches.GiftMail));
        Patch(h,typeof(GameController),"GameStart",postfix:nameof(Patches.GiftLogin));
        Patch(h,typeof(DialogueController),"GetAllAttachments",prefix:nameof(Patches.GiftAttachments));
        Patch(h,typeof(LocationLifecycleService),"HandleCoopExtract",prefix:nameof(Patches.CoopBenefit));
        Patch(h,typeof(LocationLifecycleService),"SendCoopTakenFenceMessage",prefix:nameof(Patches.CoopBenefit));
        Patch(h,typeof(TraderController),"GetAllTraders",postfix:nameof(Patches.Traders));
        Patch(h,typeof(TraderAssortHelper),"TraderAssortsHaveExpired",prefix:nameof(Patches.AssortExpiry));
        Patch(h,typeof(TraderPurchasePersisterService),"RemoveStalePurchasesFromProfiles",prefix:nameof(Patches.KeepRaidPurchases));
        Patch(h,typeof(FenceService),"GenerateFenceAssorts",postfix:nameof(Patches.FenceGenerated));
        Patch(h,typeof(FenceService),"AddItemsToFenceAssort",prefix:nameof(Patches.FenceResaleStock));
        Patch(h,typeof(FenceService),"GetFenceAssorts",postfix:nameof(Patches.FenceAccess));
        Patch(h,typeof(FenceService),"NeedsPartialRefresh",prefix:nameof(Patches.FencePartial));
        Patch(h,typeof(TraderController),"GetAssort",prefix:nameof(Patches.AssortAccess));
        Patch(h,typeof(QuestController),"CompleteQuest",postfix:nameof(Patches.RefreshProgression));
        Patch(h,typeof(RewardHelper),nameof(RewardHelper.GetRewardProductionMatch),prefix:nameof(Patches.AuthoredCraftReward));
        InstallSupplyPatches(h);
        Patch(h,typeof(TradeController),"ConfirmRagfairTrading",prefix:nameof(Patches.Flea));
        Patch(h,typeof(RagfairController),"AddPlayerOffer",prefix:nameof(Patches.Flea));
        Patch(h,typeof(RagfairController),"GetOffers",prefix:nameof(Patches.FleaSearch));
        Patch(h,typeof(LocationLifecycleService),"StartLocalRaid",nameof(Patches.Start),nameof(Patches.Started));
        Patch(h,typeof(BotCallbacks),"GetBotCap",prefix:nameof(Patches.RaidBotCap));
        Patch(h,typeof(LocationLifecycleService),"EndLocalRaid",nameof(Patches.EndReceived),nameof(Patches.EndSaved));
        h.Patch(AccessTools.Method(typeof(ItemEventRouter),"HandleEvents"),prefix:new HarmonyMethod(typeof(Patches),nameof(Patches.ItemsRequest)),postfix:new HarmonyMethod(typeof(Patches),nameof(Patches.ItemsResponse)),finalizer:new HarmonyMethod(typeof(Patches),nameof(Patches.ItemsFinished)));
        Patch(h,typeof(BotController),"Generate",nameof(Patches.BotRequest),nameof(Patches.Bots));
        Patch(h,typeof(LocationLootGenerator),"GenerateLocationLoot",postfix:nameof(Patches.Loot));
        Patch(h,typeof(LootGenerator),"CreateRandomLoot",postfix:nameof(Patches.Rewards));
        Patch(h,typeof(ScavCaseRewardGenerator),"Generate",postfix:nameof(Patches.ScavCaseRewards));
        Patch(h,typeof(LootGenerator),"GetSealedWeaponCaseLoot",postfix:nameof(Patches.RewardList));
        Patch(h,typeof(LootGenerator),"GetRandomLootContainerLoot",postfix:nameof(Patches.RewardList));
        Patch(h,typeof(RepeatableQuestController),"GetClientRepeatableQuests",postfix:nameof(Patches.Daily));
    }
}

public static class Patches
{
    public static void BareReceiverReward(HideoutProduction __0, List<List<Item>> __result) => S.StripReceiverPreset(__0, __result);
    private static SurvivalService S=>SurvivalService.Current;
    // Fence's descriptor is required by native PlayerAIData even for the player.
    // Keep descriptors for engine references; the client filters both trader lists.
    public static bool TraderRepairAccess(MongoId __0,TraderRepairActionDataRequest __1,ref ItemEventRouterResponse __result)=>S.AllowTraderRepair(__0,__1,ref __result);
    public static bool KitRepairAccess(MongoId __0,RepairActionDataRequest __1,PmcData __2,ref ItemEventRouterResponse __result)=>S.AllowKitRepair(__0,__2,__1,ref __result);
    public static void AuthoredLocales(string? __0,LocaleService __instance,ref Dictionary<string,string> __result)=>S.RestoreAuthoredLocales(string.IsNullOrEmpty(__0)?__instance.GetDesiredGameLocale():__0,__result);
    public static bool CatalogGift(MongoId __0,string __1,ref GiftSentResult __result)=>S.AllowCatalogGift(__0,__1,ref __result);
    public static bool GiftMail(SPTarkov.Server.Core.Models.Spt.Dialog.SendMessageDetails __0)=>S.AllowGiftMail(__0);
    public static void GiftLogin(MongoId sessionId)=>S.QuarantinePendingVanillaGifts(sessionId);
    public static void GiftAttachments(MongoId __1)=>S.QuarantinePendingVanillaGifts(__1);
    public static bool CoopBenefit(MongoId __0)=>S.AllowVanillaCoopBenefit(__0);
    public static void Traders(MongoId sessionId,ref List<TraderBase> __result){S.EnsureProfile(sessionId);}
    public static void RefreshProgression(MongoId sessionId)=>S.EnsureProfile(sessionId);
    public static bool AuthoredCraftReward(Reward __0,MongoId __1,ref List<HideoutProduction> __result)
    {
        var matches=S.MatchAuthoredCraftReward(__0,__1);
        if(matches is null)return true;
        __result=matches;
        return false;
    }
    public static bool AssortAccess(MongoId sessionId,MongoId traderId,ref TraderAssort __result)
    {S.EnsureProfile(sessionId);if(!S.IsOpeningTrader(traderId.ToString())||S.ContactAvailable(sessionId,traderId.ToString()))return true;__result=S.EmptyOpeningAssort();return false;}
    public static bool Trade(PmcData pmcData, ProcessBaseTradeRequestData request, MongoId sessionID,
        ref ItemEventRouterResponse __result, ref bool __state)
    {
        if (!S.TraderAvailable(pmcData, request.TransactionId.ToString(), sessionID))
        {
            __result = S.Denied(sessionID);
            return false;
        }
        return S.AllowRoubleSale(pmcData, request, sessionID, ref __result) &&
            S.CheckSupplyPurchase(pmcData, request, sessionID, ref __result, ref __state);
    }
    public static bool StartSupplyCraft(PmcData __0, HideoutSingleProductionStartRequestData __1, MongoId __2,
        ref ItemEventRouterResponse __result, ref bool __state)
        => S.CheckSupplyCraft(__0, __1, __2, ref __result, ref __state);
    public static bool ClaimSupplyCraft(PmcData __0, HideoutTakeProductionRequestData __1, MongoId __2,
        ref ItemEventRouterResponse __result, ref bool __state)
        => S.CheckSupplyClaim(__0, __1, __2, ref __result, ref __state);
    public static void SupplyPurchase(ProcessBaseTradeRequestData __1, MongoId __2, ItemEventRouterResponse __result, bool __state)
    { if (__state) S.RecordSupplyPurchase(__2, __1, __result); }
    public static void SupplyCraftClaimed(PmcData __0, HideoutTakeProductionRequestData __1, MongoId __2,
        ItemEventRouterResponse __result, bool __state)
    { if (__state) S.RecordSupplyClaim(__0, __1.RecipeId, __2, __result); }
    public static void ReleaseSupply(bool __state) { if (__state) S.ReleaseSupplyAction(); }
    public static bool Flea(MongoId sessionID,ref ItemEventRouterResponse __result){__result=S.Denied(sessionID);return false;}
    public static bool FleaSearch(ref GetOffersResult __result){__result=new GetOffersResult{Offers=[],OffersCount=0,Categories=[]};return false;}
    public static void Start(MongoId sessionId,StartLocalRaidRequestData request)=>S.Start(sessionId,request);
    public static bool RaidBotCap(string __0,MongoId __2,ref ValueTask<string> __result)
    {
        var map=__0[(__0.LastIndexOf('/')+1)..].ToLowerInvariant();
        if(S.FactoryRaidCap(__2,map) is not { } cap)return true;
        __result=ValueTask.FromResult(cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return false;
    }
    public static void Started(MongoId sessionId,StartLocalRaidRequestData request,ref StartLocalRaidResponseData __result)
    {
        var map=request.Location.ToLowerInvariant();
        __result.LocationLoot=S.ConfigureLocation(__result.LocationLoot,map,sessionId);
        S.SnapshotLabRaid(sessionId,map,__result.ServerId);
        S.SnapshotAbundanceRaid(sessionId,map,__result.ServerId,request.TransitionType is TransitionType.COMMON or TransitionType.EVENT);
    }
    public static void EndReceived(MongoId sessionId,EndLocalRaidRequestData request)=>S.DiagnosticRaidEnd(sessionId,request,false);
    public static void EndSaved(MongoId sessionId,EndLocalRaidRequestData request){S.CreditAbundanceKills(sessionId,request);S.SaveLabRaid(sessionId,request);S.RestockAfterRaid(sessionId,request);S.DiagnosticRaidEnd(sessionId,null,true);}
    public static bool AssortExpiry(MongoId __0,ref bool __result){if(!S.RaidRestockEnabled||!S.ActiveTrader(__0))return true;__result=false;return false;}
    public static bool KeepRaidPurchases(MongoId __0)=>!S.RaidRestockEnabled||!S.ActiveTrader(__0);
    public static void FenceGenerated()=>S.RebalanceFenceStock();
    public static bool FenceResaleStock(IEnumerable<Item> __0,Item __1)=>S.AllowFenceResaleStock(__0,__1);
    public static void FenceAccess(PmcData pmcProfile,ref TraderAssort __result)=>__result=S.EnforceFenceLoyalty(pmcProfile,__result);
    public static bool FencePartial(ref bool __result){if(!S.RaidRestockEnabled||!S.FenceEnabled)return true;__result=false;return false;}
    public static void ItemsRequest(MongoId sessionID,ItemEventRouterRequest info,out SurvivalService.ItemEventOperation? __state)
    {__state=S.EnterItemEvent();S.DiagnosticItemRequest(sessionID,info);}
    public static void ItemsResponse(MongoId sessionID,ref ValueTask<ItemEventRouterResponse> __result,SurvivalService.ItemEventOperation? __state)=>__result=S.DiagnosticItemResponse(__result,sessionID,__state);
    public static Exception? ItemsFinished(Exception? __exception,SurvivalService.ItemEventOperation? __state)
    {if(__exception is not null)__state?.Release();return __exception;}
    public static void BotRequest(GenerateBotsRequestData request)=>S.ConstrainRequest(request);
    public static void Bots(MongoId sessionId,ref Task<IEnumerable<BotBase?>> __result)=>__result=S.ScaleBots(__result,sessionId);
    public static void Loot(string locationId,ref List<SpawnpointTemplate> __result)=>__result=S.FilterLoot(__result,locationId);
    public static void Rewards(ref IEnumerable<List<Item>> __result)=>__result=S.FilterRewards(__result);
    public static void ScavCaseRewards(ref IEnumerable<List<Item>> __result)=>__result=S.FilterScavCaseRewards(__result);
    public static void RewardList(ref List<List<Item>> __result)=>__result=S.FilterRewards(__result);
    public static void Daily(ref List<PmcDataRepeatableQuest> __result)=>__result=[];
}
[Injectable(TypePriority=OnLoadOrder.PostDBModLoader+100)]
public sealed class Register(SurvivalService service):IOnLoad {public Task OnLoad(){try{service.Register();}catch(Exception e){service.ReportStartupFailure("register",e);throw;}return Task.CompletedTask;}}
[Injectable(TypePriority=OnLoadOrder.PostSptModLoader+5000)]
public sealed class Apply(SurvivalService service):IOnLoad {public Task OnLoad(){try{service.Apply();}catch(Exception e){service.ReportStartupFailure("apply",e);throw;}return Task.CompletedTask;}}
