using System.Text.Json.Nodes;
using System.Globalization;
namespace Codex.ZombieSurvival;

public static class LegacyHealthPolicy
{
    private static readonly Dictionary<string, double> Legacy = new() {
        ["Head"] = 150, ["Chest"] = 200, ["Stomach"] = 150,
        ["LeftArm"] = 100, ["RightArm"] = 100, ["LeftLeg"] = 125, ["RightLeg"] = 125
    };
    private static readonly Dictionary<string, double> Standard = new() {
        ["Head"] = 35, ["Chest"] = 85, ["Stomach"] = 70,
        ["LeftArm"] = 60, ["RightArm"] = 60, ["LeftLeg"] = 65, ["RightLeg"] = 65
    };
    private static double? Number(JsonNode? node) => node is JsonValue && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    public static bool Normalize(JsonObject health)
    {
        if (health["BodyParts"] is not JsonObject parts) return false;
        // Only the known old SVM preset is migrated; unrelated custom profiles are preserved.
        var currentValues = new Dictionary<string, double>();
        foreach (var (part, maximum) in Legacy)
        {
            if (parts[part]?["Health"] is not JsonObject h
                || Number(h["Maximum"]) != maximum || Number(h["Current"]) is not { } current
                || !double.IsFinite(current) || current < 0 || current > maximum) return false;
            currentValues[part] = current;
        }
        foreach (var (part, maximum) in Legacy)
        {
            var h = parts[part]!["Health"]!;
            h["Current"] = currentValues[part] * Standard[part] / maximum;
            h["Maximum"] = Standard[part];
        }
        return true;
    }
}
