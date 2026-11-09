using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private const int PointsPerTrainingLevel = 100;
    private const int EliteTrainingProgress = 51 * PointsPerTrainingLevel;
    private readonly Dictionary<MongoId, Dictionary<SkillTypes, int>> _campaignTraining = [];

    private void LoadCampaignTraining()
    {
        if ((bool?)Config["campaignEnabled"] != true) return;

        var tracks = _campaign["skillTraining"]!.AsArray().Select(track => (
            Skills: track!["skills"]!.AsArray().Select(skill => Enum.Parse<SkillTypes>(skill!.GetValue<string>())).ToArray(),
            EliteQuest: (int)track["eliteQuest"]!))
            .ToArray();

        // Later main quests also carry earlier targets so existing saves can catch up
        // through their next completion without reissuing any old quest rewards.
        foreach (var row in _campaign["main"]!.AsArray())
        {
            var index = (int)row!["index"]!;
            var floors = new Dictionary<SkillTypes, int>();
            foreach (var (skills, eliteQuest) in tracks)
            {
                var floor = EliteTrainingProgress * Math.Min(index, eliteQuest) / eliteQuest;
                foreach (var skill in skills) floors.Add(skill, floor);
            }
            _campaignTraining.Add((MongoId)row["id"]!.GetValue<string>(), floors);
        }
    }

    internal Quest WithCampaignTraining(Quest quest, PmcData profile)
    {
        const string success = nameof(QuestStatusEnum.Success);
        if (!_campaignTraining.TryGetValue(quest.Id, out var floors)
            || quest.Rewards is not { } existingRewards || !existingRewards.TryGetValue(success, out var original)
            || profile.Skills?.Common is not { } skills)
            return quest;

        var multipliers = configs.GetConfig<InventoryConfig>().SkillGainMultipliers;
        var gameEdition = profile.Info?.GameVersion;
        List<Reward>? rewards = null;
        var nextIndex = original.Select(reward => reward.Index ?? 0).DefaultIfEmpty(-1).Max() + 1;
        foreach (var skill in skills)
        {
            if (!floors.TryGetValue(skill.Id, out var floor) || skill.Progress >= floor) continue;

            var target = skill.Id.ToString();
            var multiplier = multipliers.GetValueOrDefault(target, 1.0);
            if (multiplier <= 0) continue;

            var ordinaryGain = original.Where(reward => reward.Type == RewardType.Skill && reward.Target == target
                && (reward.AvailableInGameEditions is not { Count: > 0 } || reward.AvailableInGameEditions.Contains(gameEdition!))
                && reward.NotAvailableInGameEditions?.Contains(gameEdition!) != true)
                .Sum(reward => reward.Value ?? 0) * multiplier;
            var missing = floor - skill.Progress - ordinaryGain;
            if (missing <= 0) continue;

            rewards ??= [.. original];
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"ember-training:{quest.Id}:{target}"));
            rewards.Add(new Reward
            {
                Id = (MongoId)Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant(),
                Index = nextIndex++,
                Type = RewardType.Skill,
                Target = target,
                Value = Math.Ceiling(missing / multiplier)
            });
        }

        return rewards is null ? quest : quest with { Rewards = new(existingRewards) { [success] = rewards } };
    }

    private void AddCampaignTrainingNotes(JsonObject locales)
    {
        foreach (var (quest, floors) in _campaignTraining)
        {
            var min = floors.Values.Min() / PointsPerTrainingLevel;
            var max = floors.Values.Max() / PointsPerTrainingLevel;
            var levels = min == max ? min.ToString() : $"{min}–{max}";
            var key = quest + " description";
            foreach (var (language, entries) in locales)
            {
                if (entries?[key] is not JsonValue description) continue;
                var note = language is "ch" or "zh" or "zh-cn"
                    ? $"主线训练　{floors.Count} 项技能保底至 {levels} 级"
                    : $"Campaign training   {floors.Count} skills to a minimum of level {levels}";
                entries[key] = description.GetValue<string>() + "\n\n" + note;
            }
        }
    }
}

public static partial class Patches
{
    public static void TrainingQuest(MongoId __0, PmcData __1, ref Quest? __result)
    {
        if (__result is not null) __result = S.WithCampaignTraining(__result, __1);
    }
}
