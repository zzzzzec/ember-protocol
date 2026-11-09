using System.Text.Json.Nodes;

namespace Codex.ZombieSurvival;

public sealed partial class SurvivalService
{
    private void RegisterQuestIllustrations()
    {
        foreach (var asset in Read("data/quest-illustrations.json")["assets"]!.AsArray().OfType<JsonObject>())
            images.AddRoute(asset["route"]!.ToString(), Path.Combine(Folder, "data", asset["file"]!.ToString()));
    }
}
