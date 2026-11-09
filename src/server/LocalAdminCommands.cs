using Path = System.IO.Path;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Codex.ZombieSurvival;

// Local filesystem commands are consumed once, before clients can connect.
// This service deliberately exposes no network route and never polls during raids.
[Injectable(TypePriority=OnLoadOrder.PostSptModLoader+6000)]
public sealed class LocalAdminCommands(SurvivalService survival, SaveServer saves, DatabaseService db,
    MailSendService mail, ItemHelper itemHelper, JsonUtil json, ISptLogger<LocalAdminCommands> log):IOnLoad
{
    private string Root => Path.GetFullPath(Path.Combine(survival.Folder,"..","..","..",".."));
    public async Task OnLoad()
    {
        var admin=Path.Combine(Root,".sptz","admin");var inbox=Path.Combine(admin,"inbox");
        if(!Directory.Exists(inbox))return;
        Directory.CreateDirectory(Path.Combine(admin,"receipts"));Directory.CreateDirectory(Path.Combine(admin,"processed"));Directory.CreateDirectory(Path.Combine(admin,"failed"));
        foreach(var file in Directory.EnumerateFiles(inbox,"*.json").OrderBy(QueuedAt).ThenBy(p=>p,StringComparer.Ordinal).Take(100))
        {
            var id=Path.GetFileNameWithoutExtension(file);MongoId session=default;SptProfile? original=null;bool saved=false;
            try
            {
                if(!Regex.IsMatch(id,"^[a-f0-9]{32}$"))throw new InvalidDataException("Command filename must be a lowercase GUID.");
                if(new FileInfo(file).Length>65536)throw new InvalidDataException("Command exceeds 64 KiB.");
                var raw=File.ReadAllText(file);var cmd=JsonNode.Parse(raw)!.AsObject();
                if((int?)cmd["formatVersion"]!=1||cmd["id"]?.ToString()!=id)throw new InvalidDataException("Invalid command identity/version.");
                if(cmd["instanceKind"]?.ToString()!=survival.Config["instanceKind"]?.ToString())throw new InvalidDataException("Instance role mismatch.");
                var target=cmd["profileId"]?.ToString()??"";
                if(!Regex.IsMatch(target,"^[a-f0-9]{24}$"))throw new InvalidDataException("Invalid profile ID.");
                session=(MongoId)target;
                if(!saves.GetProfiles().TryGetValue(session,out var profile)||profile.CharacterData?.PmcData is null)throw new InvalidDataException("Unknown or uncreated character.");
                if(saves.IsProfileInvalidOrUnloadable(session))throw new InvalidDataException("Target profile is marked invalid or unloadable.");
                var ledger="codex.ember.admin."+id;
                if(profile.SptData?.Migrations?.ContainsKey(ledger)==true)
                {Receipt(admin,id,"AlreadyApplied",null);File.Move(file,Path.Combine(admin,"processed",id+".json"),true);continue;}
                var action=cmd["action"]?.ToString();List<Item>? items=null;QuestStatus? quest=null;QuestStatusEnum? state=null;
                if(action=="grant")items=BuildItems(cmd);
                else if(action=="quest")
                {
                    var qid=cmd["questId"]?.ToString()??"";
                    if(!survival.Read("data/quests.json").AsObject().ContainsKey(qid))throw new InvalidDataException("Only authored campaign quests are supported.");
                    quest=profile.CharacterData.PmcData.Quests?.FirstOrDefault(q=>q.QId.ToString()==qid);
                    if(quest is null||quest.Status is not (QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish))throw new InvalidDataException("Quest must be accepted and unfinished; completed/failed/locked quests cannot be reset.");
                    state=cmd["state"]?.ToString() switch {"ready"=>QuestStatusEnum.AvailableForFinish,"success"=>QuestStatusEnum.Success,_=>throw new InvalidDataException("Unknown quest state.")};
                }
                else throw new InvalidDataException("Unknown admin action.");
                var before=json.Serialize(profile);original=json.Deserialize<SptProfile>(before)!;
                var backup=Path.Combine(admin,"backups",id);Directory.CreateDirectory(backup);
                File.WriteAllText(Path.Combine(backup,"profile-before.json"),before);
                File.WriteAllText(Path.Combine(backup,"command.json"),raw);
                if(items is not null)
                {
                    var note=cmd["note"]?.ToString()??"管理补给";
                    if(note.Length>500)throw new InvalidDataException("Note exceeds 500 characters.");
                    mail.SendDirectNpcMessageToPlayer(session,survival.TraderId,MessageType.NpcTraderMessage,note,items,31536000);
                }
                else
                {
                    quest!.Status=state!.Value;quest.StatusTimers??=[];quest.StatusTimers[state.Value]=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    quest.CompletedConditions??=[];
                    var def=survival.Read("data/quests.json")[quest.QId.ToString()]!;
                    foreach(var condition in def["conditions"]!["AvailableForFinish"]!.AsArray())
                    {var cid=condition!["id"]!.ToString();if(!quest.CompletedConditions.Contains(cid))quest.CompletedConditions.Add(cid);}
                    survival.EnsureProfile(session);
                }
                profile.SptData??=new Spt();profile.SptData.Migrations??=[];
                profile.SptData.Migrations[ledger]=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await saves.SaveProfileAsync(session);saved=true;
                Receipt(admin,id,"Applied",new {action,profile=target,backup,items=items?.Count,quest=quest?.QId.ToString(),questState=state?.ToString(),questRewardsGranted=false,sha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()});
                File.Move(file,Path.Combine(admin,"processed",id+".json"),true);
                log.Success("[SPT-Z] Local admin command applied: "+id+" ("+action+").",null);
            }
            catch(Exception e)
            {
                if(original is not null&&!saved)saves.GetProfiles()[session]=original;
                try{Receipt(admin,id,saved?"AppliedReceiptError":"Rejected",new {error=e.Message});File.Move(file,Path.Combine(admin,"failed",Path.GetFileName(file)),true);}catch { }
                log.Warning("[SPT-Z] Local admin command "+id+": "+e.Message,null);
            }
        }
    }
    // GUID filenames are identities, not enqueue order. Legacy/invalid commands still
    // enter normal validation using their filesystem creation time as a fallback.
    private static DateTimeOffset QueuedAt(string file)
    {
        try
        {
            if(new FileInfo(file).Length<=65536)
            {
                var value=JsonNode.Parse(File.ReadAllText(file))?["createdUtc"]?.ToString();
                if(DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var when))return when;
            }
        }
        catch { }
        return new DateTimeOffset(File.GetCreationTimeUtc(file));
    }
    private List<Item> BuildItems(JsonObject cmd)
    {
        var tpl=cmd["itemTpl"]?.ToString()??"";var count=(int?)cmd["count"]??0;
        if(!Regex.IsMatch(tpl,"^[a-f0-9]{24}$")||!db.GetItems().TryGetValue((MongoId)tpl,out var template)||template.Type!="Item"||template.Properties?.QuestItem==true)throw new InvalidDataException("Unknown or unsupported item template.");
        var max=Math.Max(1,template.Properties?.StackMaxSize??1);
        if(count<1||count>1000000||(long)Math.Ceiling(count/(double)max)>100)throw new InvalidDataException("Count must produce between one and one hundred stacks, at most one million units.");
        var presets=JsonNode.Parse(json.Serialize(db.GetGlobals())!)!["ItemPresets"]!.AsObject();
        var preset=presets.Select(p=>p.Value!).FirstOrDefault(p=>p["_encyclopedia"]?.ToString()==tpl)?["_items"]?.AsArray();
        var nodes=preset??new JsonArray(new JsonObject{["_id"]=new MongoId().ToString(),["_tpl"]=tpl});
        if(preset is null&&JsonNode.Parse(json.Serialize(template)!)?["_props"]?["weapClass"] is not null)throw new InvalidDataException("Weapon requires a complete native preset.");
        var root=nodes.First(i=>i!["_tpl"]!.ToString()==tpl)!["_id"]!.ToString();var result=new List<Item>();
        while(count>0)
        {
            var batch=Math.Min(count,max);count-=batch;
            var ids=nodes.ToDictionary(i=>i!["_id"]!.ToString(),_=>new MongoId());
            foreach(var node in nodes)
            {
                var item=json.Deserialize<Item>(node!.ToJsonString())!;var old=node["_id"]!.ToString();item.Id=ids[old];
                if(old==root){item.ParentId=null;item.SlotId=null;item.Location=null;}
                else item.ParentId=ids[item.ParentId!].ToString();
                item.Upd=itemHelper.GenerateUpdForItem(db.GetItems()[item.Template]);
                item.Upd.SpawnedInSession=false;if(old==root)item.Upd.StackObjectsCount=batch;
                result.Add(item);
            }
        }
        return result;
    }
    private static void Receipt(string root,string id,string status,object? details)
    {
        var record=new{formatVersion=1,id,status,utc=DateTimeOffset.UtcNow,details};var value=System.Text.Json.JsonSerializer.Serialize(record,new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
        var path=Path.Combine(root,"receipts",id+".json");File.WriteAllText(path+".tmp",value);File.Move(path+".tmp",path,true);
        File.AppendAllText(Path.Combine(root,"audit.jsonl"),System.Text.Json.JsonSerializer.Serialize(record)+Environment.NewLine);
    }
}
