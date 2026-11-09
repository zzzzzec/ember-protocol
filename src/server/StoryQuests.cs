using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private JsonObject _stories=new();
    private readonly object _storyGate=new();
    private readonly Dictionary<(MongoId Quest, MongoId Condition), JsonObject> _authoredFirHandovers = [];
    private const string StoryChoiceKey="emberStoryChoices";
    private bool StoryQuest(MongoId id)=>_stories["quests"]!.AsArray().Any(x=>x!.ToString()==id.ToString());
    private JsonObject StoryDefinition(MongoId id)=>Node(db.GetQuests()[id]).AsObject();
    private JsonObject StoryLedger(SptProfile profile)
    {
        profile.SptData??=new Spt();profile.SptData.ExtensionData??=[];
        if(profile.SptData.ExtensionData.TryGetValue(StoryChoiceKey,out var raw))
        {
            var n=raw switch{JsonObject o=>o,JsonElement e=>JsonNode.Parse(e.GetRawText()),_=>JsonSerializer.SerializeToNode(raw)};
            if(n is JsonObject result){profile.SptData.ExtensionData[StoryChoiceKey]=result;return result;}
        }
        var ledger=new JsonObject();profile.SptData.ExtensionData[StoryChoiceKey]=ledger;return ledger;
    }
    private (JsonObject Group,JsonObject Route)? StoryRoute(MongoId quest)
    {
        foreach(var group in _stories["choices"]!.AsArray().OfType<JsonObject>())
            foreach(var route in group["routes"]!.AsArray().OfType<JsonObject>())
                if(route["quests"]!.AsArray().Any(q=>q!.ToString()==quest.ToString()))return (group,route);
        return null;
    }
    private string? StoryChosen(MongoId session,JsonObject group)
    {
        var profile=saves.GetProfile(session);var choice=StoryLedger(profile)[group["id"]!.ToString()]?["quest"]?.ToString();
        if(choice is not null)return choice;
        // Legacy transport commits on acceptance; a new prepared draft is not a sent reply.
        var choices=group["routes"]!.AsArray().Select(r=>r!["choice"]!.ToString()).ToHashSet();
        return profile.CharacterData?.PmcData?.Quests?.Where(q=>choices.Contains(q.QId.ToString())&&StoryQuestPolicy.RecoverChoice(group,(int)q.Status,q.StartTime)).OrderBy(q=>q.StartTime).FirstOrDefault()?.QId.ToString();
    }
    private string? StoryChoiceError(MongoId session,MongoId quest)
    {
        if(StoryRoute(quest) is not { } info)return null;
        var chosen=StoryChosen(session,info.Group);
        return chosen is not null&&chosen!=info.Route["choice"]!.ToString()?info.Group["rejectionMessage"]?.ToString()??"这辆车已经答应给另一边了，这份单子不能再接。":null;
    }
    private void InstallStoryPatches(Harmony h)
    {
        _stories=Read("data/story-quests.json").AsObject();
        foreach (var (questId, quest) in Read("data/quests.json").AsObject())
            foreach (var condition in quest!["conditions"]!["AvailableForFinish"]!.AsArray().OfType<JsonObject>())
                if (condition["conditionType"]?.ToString() == "HandoverItem" && (bool?)condition["onlyFoundInRaid"] == true)
                    _authoredFirHandovers.Add(((MongoId)questId, (MongoId)condition["id"]!.ToString()), condition);
        void Hook(string method,string before,string after)=>h.Patch(AccessTools.Method(typeof(QuestController),method),prefix:new HarmonyMethod(typeof(StoryPatches),before),postfix:new HarmonyMethod(typeof(StoryPatches),after),finalizer:new HarmonyMethod(typeof(StoryPatches),nameof(StoryPatches.Finished)));
        Hook("AcceptQuest",nameof(StoryPatches.Accept),nameof(StoryPatches.Accepted));
        Hook("HandoverQuest",nameof(StoryPatches.Handover),nameof(StoryPatches.HandedOver));
        Hook("CompleteQuest",nameof(StoryPatches.Complete),nameof(StoryPatches.Completed));
        Hook("FailQuest",nameof(StoryPatches.Fail),nameof(StoryPatches.Failed));
        h.Patch(AccessTools.Method(typeof(QuestController),"GetClientQuests"),postfix:new HarmonyMethod(typeof(StoryPatches),nameof(StoryPatches.Visible)));
    }
    public sealed class StoryOperation {public object Gate=null!;public MongoId Quest;public MongoId Session;public bool Native;}
    private StoryOperation? EnterStory(MongoId quest,MongoId session,bool includeAuthored=false)
    {
        if(!StoryQuest(quest)&&!(includeAuthored&&_authoredQuestIds.Contains(quest)))return null;
        Monitor.Enter(_storyGate);return new StoryOperation{Gate=_storyGate,Quest=quest,Session=session};
    }
    private bool StoryReject(StoryOperation state,string reason,ref ItemEventRouterResponse result)
    {result=FieldDenied(state.Session,reason);Trace("story.rejected",state.Session,new{quest=state.Quest.ToString(),reason});return false;}
    public bool BeforeStoryAccept(PmcData pmc,AcceptQuestRequestData request,MongoId session,ref ItemEventRouterResponse result,out StoryOperation? state)
    {
        state=EnterStory(request.QuestId,session,true);if(state is null)return true;
        if(!StoryQuest(request.QuestId))
        {state.Native=AllowAuthoredQuestAccept(pmc,request,session,ref result);return state.Native;}
        var existing=pmc.Quests?.FirstOrDefault(q=>q.QId==request.QuestId);
        if(existing is not null&&(int)existing.Status is 2 or 3 or 4){result=outputs.GetOutput(session);return false;}
        if(existing is not null&&(int)existing.Status>=5)return StoryReject(state,"这份委托已经结束了。",ref result);
        var reason=StoryChoiceError(session,request.QuestId)??StoryQuestPolicy.PrerequisiteError(Node(pmc).AsObject(),StoryDefinition(request.QuestId));
        if(reason is not null)return StoryReject(state,reason,ref result);
        state.Native=true;return true;
    }
    public void AfterStoryAccept(QuestController controller,PmcData pmc,StoryOperation? state)
    {
        if(state?.Native!=true||pmc.Quests?.Any(q=>q.QId==state.Quest&&(int)q.Status==2)!=true)return;
        if(StoryRoute(state.Quest) is not { } info||info.Route["choice"]!.ToString()!=state.Quest.ToString())return;
        if(StoryQuestPolicy.CommitStage(info.Group)=="complete")
        {
            saves.SaveProfileAsync(state.Session).GetAwaiter().GetResult();
            Trace("story.choice_prepared",state.Session,new{group=info.Group["id"]!.ToString(),quest=state.Quest.ToString()});return;
        }
        CommitStoryChoice(controller,pmc,state,info.Group,info.Route,"accept");
    }
    private void CommitStoryChoice(QuestController controller,PmcData pmc,StoryOperation state,JsonObject group,JsonObject route,string stage)
    {
        var profile=saves.GetProfile(state.Session);var ledger=StoryLedger(profile);var key=group["id"]!.ToString();
        if(ledger[key] is not null)return;
        var receipt=new JsonObject{["quest"]=state.Quest.ToString(),["route"]=route["id"]!.ToString(),["selectedUtc"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),["version"]=stage=="accept"?"1.4.0":Config["version"]?.ToString(),["commitStage"]=stage};
        if(route["payload"] is { } payload)
        {
            receipt["recipient"]=route["recipient"]!.DeepClone();receipt["scope"]=route["scope"]!.DeepClone();
            receipt["payloadKey"]=route["payloadKey"]!.DeepClone();receipt["payload"]=payload.DeepClone();
            receipt["payloadHash"]=StoryQuestPolicy.PayloadHash(payload.ToString());
        }
        ledger[key]=receipt;
        foreach(var other in group["routes"]!.AsArray().Where(r=>r!["choice"]!.ToString()!=state.Quest.ToString()))
        {
            var otherId=(MongoId)other!["choice"]!.ToString();var old=pmc.Quests!.FirstOrDefault(q=>q.QId==otherId);
            if(old is null){old=new QuestStatus{QId=otherId,StartTime=0,Status=QuestStatusEnum.AvailableForStart,StatusTimers=[]};pmc.Quests.Add(old);}
            if((int)old.Status!=5)controller.FailQuest(pmc,new FailQuestRequestData{Action="QuestFail",QuestId=otherId,RemoveExcessItems=true},state.Session,outputs.GetOutput(state.Session));
            var changes=outputs.GetOutput(state.Session).ProfileChanges[state.Session].QuestsStatus;
            if(!changes.Any(q=>q.QId==otherId))changes.Add(old);
        }
        saves.SaveProfileAsync(state.Session).GetAwaiter().GetResult();
        Trace("story.choice_committed",state.Session,new{group=key,route=route["id"]!.ToString(),quest=state.Quest.ToString(),stage,payloadHash=receipt["payloadHash"]?.ToString()});
    }
    public void AfterStoryComplete(QuestController controller,PmcData pmc,StoryOperation? state)
    {
        if(state?.Native==true&&pmc.Quests?.Any(q=>q.QId==state.Quest&&(int)q.Status==4)==true&&StoryRoute(state.Quest) is { } info&&info.Route["choice"]!.ToString()==state.Quest.ToString()&&StoryQuestPolicy.CommitStage(info.Group)=="complete")
            CommitStoryChoice(controller,pmc,state,info.Group,info.Route,"complete");
        if(state?.Native==true)OnLabStoryCompleted(state.Session,state.Quest);
        AfterStoryProgress(state,"story.completed");
    }
    public bool BeforeStoryHandover(PmcData pmc,HandoverQuestRequestData request,MongoId session,ref ItemEventRouterResponse result,out StoryOperation? state)
    {
        var authoredFir = _authoredFirHandovers.TryGetValue((request.QuestId, request.ConditionId), out var condition);
        state=EnterStory(request.QuestId,session,authoredFir);if(state is null)return true;
        var reason=StoryChoiceError(session,request.QuestId);
        if(reason is not null)return StoryReject(state,reason,ref result);
        if(pmc.Quests?.Any(q=>q.QId==request.QuestId&&(int)q.Status is 2 or 3)!=true)return StoryReject(state,"先接下这份委托。",ref result);
        condition ??= StoryDefinition(request.QuestId)["conditions"]!["AvailableForFinish"]!.AsArray()
            .OfType<JsonObject>().FirstOrDefault(c => c["id"]!.ToString() == request.ConditionId.ToString());
        if(condition?["conditionType"]?.ToString()!="HandoverItem")return StoryReject(state,"这不是这份委托的交付项目。",ref result);
        var player=Node(pmc).AsObject();
        if(FieldQuestPolicy.ObjectiveReady(player,condition,request.QuestId.ToString())){result=outputs.GetOutput(session);return false;}
        reason=StoryQuestPolicy.HandoverError(player,condition,Node(request.Items??[]).AsArray());
        if(reason is not null)return StoryReject(state,reason,ref result);
        state.Native=true;return true;
    }
    public bool BeforeStoryComplete(PmcData pmc,CompleteQuestRequestData request,MongoId session,ref ItemEventRouterResponse result,out StoryOperation? state)
    {
        state=EnterStory(request.QuestId,session,true);if(state is null)return true;
        var status=pmc.Quests?.FirstOrDefault(q=>q.QId==request.QuestId)?.Status;
        if((int?)status==4)
        {result=outputs.GetOutput(session);Trace("quest.complete_replayed",session,new{quest=request.QuestId.ToString()});return false;}
        if((int?)status is not (2 or 3))return StoryReject(state,"这份委托当前不能结算。",ref result);
        if(!StoryQuest(request.QuestId)){state.Native=true;return true;}
        if((request.QuestId==_labRepair||request.QuestId==_labScout)&&!LabObjectiveVerified(session,request.QuestId))return StoryReject(state,"在同一局操作实验室配电箱并存活撤离。",ref result);
        var reason=StoryChoiceError(session,request.QuestId);
        if(reason is not null)return StoryReject(state,reason,ref result);
        if(!StoryQuestPolicy.ObjectivesReady(Node(pmc).AsObject(),StoryDefinition(request.QuestId)))return StoryReject(state,"先把清单和作战目标办完。",ref result);
        state.Native=true;return true;
    }
    public bool BeforeStoryFail(PmcData pmc,FailQuestRequestData request,MongoId session,ref ItemEventRouterResponse result,out StoryOperation? state)
    {
        state=EnterStory(request.QuestId,session);if(state is null)return true;
        var status=pmc.Quests?.FirstOrDefault(q=>q.QId==request.QuestId)?.Status;
        if((int?)status>=4)
        {result=outputs.GetOutput(session);Trace("quest.fail_replayed",session,new{quest=request.QuestId.ToString(),status=(int?)status});return false;}
        state.Native=true;return true;
    }
    public void AfterStoryProgress(StoryOperation? state,string type)
    {
        if(state?.Native!=true)return;
        if(type is "story.completed" or "story.failed")
        {
            var status=saves.GetProfile(state.Session).CharacterData?.PmcData?.Quests?.FirstOrDefault(q=>q.QId==state.Quest)?.Status;
            if((int?)status!=(type=="story.completed"?4:5))
            {Trace("quest.completion_not_applied",state.Session,new{quest=state.Quest.ToString(),status=(int?)status});return;}
        }
        saves.SaveProfileAsync(state.Session).GetAwaiter().GetResult();Trace(type,state.Session,new{quest=state.Quest.ToString()});
    }
    public List<Quest> VisibleStoryQuests(MongoId session,List<Quest> quests)
    {
        var hidden=new HashSet<string>();
        foreach(var group in _stories["choices"]!.AsArray().OfType<JsonObject>())
        {
            var chosen=StoryChosen(session,group);if(chosen is null)continue;
            foreach(var route in group["routes"]!.AsArray().Where(r=>r!["choice"]!.ToString()!=chosen))
                foreach(var q in route!["quests"]!.AsArray().Skip(1))hidden.Add(q!.ToString());
        }
        return VisibleReleasedQuests(saves.GetProfile(session).CharacterData!.PmcData!,quests.Where(q=>!hidden.Contains(q.Id.ToString())).ToList());
    }
}
public static class StoryPatches
{
    private static SurvivalService S=>SurvivalService.Current;
    public static bool Accept(PmcData __0,AcceptQuestRequestData __1,MongoId __2,ref ItemEventRouterResponse __result,out SurvivalService.StoryOperation? __state)=>S.BeforeStoryAccept(__0,__1,__2,ref __result,out __state);
    public static void Accepted(QuestController __instance,PmcData __0,SurvivalService.StoryOperation? __state)=>S.AfterStoryAccept(__instance,__0,__state);
    public static bool Handover(PmcData __0,HandoverQuestRequestData __1,MongoId __2,ref ItemEventRouterResponse __result,out SurvivalService.StoryOperation? __state)=>S.BeforeStoryHandover(__0,__1,__2,ref __result,out __state);
    public static void HandedOver(SurvivalService.StoryOperation? __state)=>S.AfterStoryProgress(__state,"story.handover");
    public static bool Complete(PmcData __0,CompleteQuestRequestData __1,MongoId __2,ref ItemEventRouterResponse __result,out SurvivalService.StoryOperation? __state)=>S.BeforeStoryComplete(__0,__1,__2,ref __result,out __state);
    public static void Completed(QuestController __instance,PmcData __0,SurvivalService.StoryOperation? __state)=>S.AfterStoryComplete(__instance,__0,__state);
    public static bool Fail(PmcData __0,FailQuestRequestData __1,MongoId __2,ref ItemEventRouterResponse __result,out SurvivalService.StoryOperation? __state)=>S.BeforeStoryFail(__0,__1,__2,ref __result,out __state);
    public static void Failed(SurvivalService.StoryOperation? __state)=>S.AfterStoryProgress(__state,"story.failed");
    public static Exception? Finished(Exception? __exception,SurvivalService.StoryOperation? __state){if(__state is not null)Monitor.Exit(__state.Gate);return __exception;}
    public static void Visible(MongoId sessionId,ref List<Quest> __result)=>__result=S.VisibleStoryQuests(sessionId,__result);
}
