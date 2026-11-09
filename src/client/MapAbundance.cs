using System;
using System.Globalization;
using System.Threading.Tasks;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using TMPro;

namespace Codex.ZombieSurvival.Client;

internal static class MapAbundance
{
    private static Task<JObject>? _pending;
    private static readonly AccessTools.FieldRef<LocationInfoPanel, LocationSettingsClass.Location> SelectedLocation =
        AccessTools.FieldRefAccess<LocationInfoPanel, LocationSettingsClass.Location>("location_0");

    internal static void Install(Harmony harmony) =>
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocationInfoPanel), nameof(LocationInfoPanel.Set)),
            postfix: new HarmonyMethod(typeof(MapAbundance), nameof(Show)));

    private static async void Show(LocationInfoPanel __instance, LocationSettingsClass.Location __0,
        TextMeshProUGUI ____description)
    {
        var map = __0.Id?.ToLowerInvariant() switch
        {
            "sandbox_high" => "sandbox",
            "factory4" or "factory4_day" or "factory4_night" => "factory",
            { } id => id,
            _ => ""
        };
        if (____description == null || map is not ("sandbox" or "bigmap" or "interchange" or "factory" or "rezervbase" or "laboratory")) return;

        var description = ____description.text;
        var request = _pending ??= Task.Run(() => JObject.Parse(RequestHandler.GetJson("/ember/map/abundance")));
        try
        {
            var data = await request;
            if (__instance == null || !__instance.isActiveAndEnabled || !ReferenceEquals(SelectedLocation(__instance), __0) || ____description == null ||
                ____description.text != description || data[map] is not JObject progress) return;
            var label = GClass2348.Localized("ember map abundance", null);
            var stats = string.Format(CultureInfo.InvariantCulture, label, Format(progress, "total"),
                Format(progress, "quests"), Format(progress, "kills"));
            ____description.text = stats + "\n\n" + description;
        }
        catch (Exception error) { Plugin.Log.LogWarning("Map abundance UI: " + error.Message); }
        finally { if (ReferenceEquals(_pending, request)) _pending = null; }
    }

    private static string Format(JObject data, string key) =>
        data.Value<decimal>(key).ToString("0.00##", CultureInfo.InvariantCulture);
}
