// Lighting and native switch approach adapted from Vultify/Blackout v3.2.1 (MIT).
// See Blackout.LICENSE for the upstream copyright and permission notice.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;
using UnityEngine.SceneManagement;
using Switch = EFT.Interactive.Switch;

namespace Codex.ZombieSurvival.Client;

internal sealed class LabBlackout : MonoBehaviour
{
    private static readonly Vector3 PanelPosition = new(-130.899f, 1.404f, -336.423f);
    private static readonly Vector3[] EmergencyPositions =
    [
        new(-226.1066f, 4.6192f, -426.5847f),
        new(-175.777f, 5.318f, -249.235f),
        new(-129.822f, -4.3899f, -244.9065f)
    ];
    private static readonly int[] EmissionFloats = [Shader.PropertyToID("_EmissionPower"), Shader.PropertyToID("_EmissionVisibility")];
    private static readonly int[] EmissionColors = [Shader.PropertyToID("_EmissionColor"), Shader.PropertyToID("_EmissiveColor"), Shader.PropertyToID("_EmAnim1Color"), Shader.PropertyToID("_EmAnim2Color")];
    private static readonly System.Reflection.FieldInfo AdvancedMultiplier = AccessTools.Field(typeof(CullingAdvancedLightObject), "float_2");
    private static LabBlackout? _active;
    private readonly List<Action> _restore = new();
    private Switch? _panel;
    private string _raid = "";
    private bool _panelPending;

    internal static void Install(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(ControlledLampGroup), "method_8", new[] { typeof(bool) });
        if (method != null) harmony.Patch(method, prefix: new HarmonyMethod(typeof(LabBlackout), nameof(KeepGroupOff)));
    }

    private static void KeepGroupOff(ref bool __0)
    {
        if (_active != null) __0 = false;
    }

    internal static void Begin(GameWorld world)
    {
        if (string.Equals(world.MainPlayer?.Location ?? world.LocationId, "laboratory", StringComparison.OrdinalIgnoreCase))
            world.gameObject.AddComponent<LabBlackout>();
    }

    private IEnumerator Start()
    {
        var request = Task.Run(() => JObject.Parse(RequestHandler.GetJson("/ember/lab/state")));
        yield return new WaitUntil(() => request.IsCompleted);
        if (request.IsFaulted)
        {
            Plugin.Log.LogWarning("[SPT-Z] Lab power state unavailable: " + request.Exception?.GetBaseException().Message);
            yield break;
        }
        var state = request.Result;
        if ((bool?)state["blackout"] != true) yield break;
        _raid = (string?)state["raid"] ?? "";
        var until = Time.realtimeSinceStartup + 6f;
        var scenes = LoadedLightScenes();
        while (scenes.Length == 0 && Time.realtimeSinceStartup < until)
        {
            yield return new WaitForSecondsRealtime(.25f);
            scenes = LoadedLightScenes();
        }

        if (scenes.Length > 0)
        {
            _active = this;
            SetAmbient();
            foreach (var scene in scenes)
                foreach (var root in scene.GetRootGameObjects())
                {
                    Darken(root);
                    yield return null;
                }
            foreach (var position in EmergencyPositions) EmergencyLight(position, 7f);
            EmergencyLight(PanelPosition + new Vector3(-.6f, 1.2f, .45f), 4f);
        }
        var operation = (string?)state["operation"] ?? "";
        var repair = operation == "repair";
        if (operation.Length > 0)
        {
            while (!CreatePanel() && Time.realtimeSinceStartup < until)
                yield return new WaitForSecondsRealtime(.25f);
        }
        if (repair && _panel != null && (string?)state["plantZone"] is { Length: > 0 } zone) CreatePlacementZone(zone);
        ClientDiagnostics.Record("lab.blackout", new { lightScenes = scenes.Length, operation, panel = _panel != null, changed = _restore.Count });
        if (scenes.Length == 0 || operation.Length > 0 && _panel == null)
            Plugin.Log.LogWarning("[SPT-Z] Lab blackout: a scene or repair panel is unavailable.");
    }

    private static Scene[] LoadedLightScenes() => Enumerable.Range(0, SceneManager.sceneCount)
        .Select(SceneManager.GetSceneAt).Where(scene => scene.isLoaded && scene.name.IndexOf("_LIGHT", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

    private void SetAmbient()
    {
        if (!Singleton<LevelSettings>.Instantiated) return;
        var level = Singleton<LevelSettings>.Instance;
        if (level == null) return;
        var intensity = level.AmbientIntensity;
        var nightIntensity = level.NightVisionAmbientIntensity;
        level.AmbientIntensity *= .08f;
        level.NightVisionAmbientIntensity *= .25f;
        _restore.Add(() => { if (level != null) { level.AmbientIntensity = intensity; level.NightVisionAmbientIntensity = nightIntensity; } });
    }

    private void Darken(GameObject root)
    {
        foreach (var cull in root.GetComponentsInChildren<CullingLightObject>(true))
        {
            var previous = cull.IntensityMultiplier;
            cull.IntensityMultiplier = 0f;
            _restore.Add(() => { if (cull != null) cull.IntensityMultiplier = previous; });
        }
        foreach (var cull in root.GetComponentsInChildren<CullingAdvancedLightObject>(true))
        {
            var previous = (float)(AdvancedMultiplier.GetValue(cull) ?? 1f);
            cull.IntensityMultiplier = 0f;
            _restore.Add(() => { if (cull != null) cull.IntensityMultiplier = previous; });
        }
        foreach (var light in root.GetComponentsInChildren<Light>(true))
        {
            var enabled = light.enabled;
            var intensity = light.intensity;
            light.enabled = false;
            light.intensity = 0;
            _restore.Add(() => { if (light != null) { light.enabled = enabled; light.intensity = intensity; } });
        }
        foreach (var light in root.GetComponentsInChildren<BaseLight>(true))
        {
            var intensity = light.m_Intensity;
            light.m_Intensity = 0;
            _restore.Add(() => { if (light != null) light.m_Intensity = intensity; });
        }
        foreach (var volume in root.GetComponentsInChildren<VolumetricLight>(true))
        {
            var enabled = volume.enabled;
            volume.enabled = false;
            _restore.Add(() => { if (volume != null) volume.enabled = enabled; });
        }
        if (root.name.IndexOf("Inverted_Shadow", StringComparison.OrdinalIgnoreCase) >= 0)
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                renderer.enabled = false;
                _restore.Add(() => { if (renderer != null) renderer.enabled = true; });
            }

        var materials = new List<Material>();
        var block = new MaterialPropertyBlock();
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            renderer.GetSharedMaterials(materials);
            for (var index = 0; index < materials.Count; index++)
            {
                var material = materials[index];
                if (material == null) continue;
                var floats = EmissionFloats.Where(material.HasProperty).ToArray();
                var colors = EmissionColors.Where(material.HasProperty).ToArray();
                if (floats.Length == 0 && colors.Length == 0) continue;
                var slot = index;
                var previous = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(previous, slot);
                block.Clear();
                renderer.GetPropertyBlock(block, slot);
                foreach (var id in floats) block.SetFloat(id, 0);
                foreach (var id in colors) block.SetColor(id, Color.black);
                renderer.SetPropertyBlock(block, slot);
                _restore.Add(() => { if (renderer != null) renderer.SetPropertyBlock(previous.isEmpty ? null : previous, slot); });
            }
        }
    }

    private void EmergencyLight(Vector3 position, float range)
    {
        var lamp = new GameObject("Ember emergency light");
        lamp.transform.SetParent(transform, false);
        lamp.transform.position = position;
        var light = lamp.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(.9f, .54f, .18f);
        light.intensity = .55f;
        light.range = range;
        light.shadows = LightShadows.None;
    }

    private void CreatePlacementZone(string zoneId)
    {
        var zone = new GameObject("Ember MS2000 placement");
        zone.transform.SetParent(transform, false);
        zone.transform.position = PanelPosition + new Vector3(-1.2f, -.35f, 0);
        var collider = zone.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = new Vector3(2.4f, 2.3f, 2.8f);
        var trigger = zone.AddComponent<PlaceItemTrigger>();
        trigger.SetId(zoneId);
        StartCoroutine(TrackPlacementZone(trigger, collider));
    }

    private static IEnumerator TrackPlacementZone(PlaceItemTrigger zone, BoxCollider volume)
    {
        // The room is built after the local player starts. Native overlap notifications
        // can miss that late-created trigger, so reconcile it only for this placement.
        var interval = new WaitForSecondsRealtime(.2f);
        while (zone != null && Singleton<GameWorld>.Instantiated)
        {
            var player = Singleton<GameWorld>.Instance.MainPlayer;
            if (player != null && player.HealthController.IsAlive)
            {
                var inside = volume.bounds.Contains(player.Position + Vector3.up * .8f);
                if (inside && player.PlaceItemZone == null)
                    zone.TriggerEnter(player);
                else if (!inside && player.PlaceItemZone == zone)
                    zone.TriggerExit(player);
            }
            yield return interval;
        }
    }

    private bool CreatePanel()
    {
        if (_panel != null) return true;
        var points = ExfiltrationControllerClass.Instance?.ExfiltrationPoints;
        var donor = points?.FirstOrDefault(point => point != null && point.Settings?.Name == "lab_Parking_Gate" && point.Switch != null)?.Switch;
        donor ??= points?.FirstOrDefault(point => point != null && point.Switch != null)?.Switch;
        if (donor == null) return false;

        var go = Instantiate(donor.gameObject, PanelPosition, Quaternion.identity, transform);
        go.name = "ember_lab_power_panel";
        var panel = go.GetComponent<Switch>();
        panel.Id = go.name;
        panel.ExfiltrationPoint = null;
        panel.Door = null;
        panel.NextSwitches = Array.Empty<Switch.SwitchAndOperation>();
        panel.PreviousSwitch = null;
        panel.AutoTurnOff = false;
        panel.DoorState = EDoorState.Shut;
        panel.Operatable = true;
        panel.OnDoorStateChanged += PanelChanged;
        _panel = panel;

        var bodyDonor = GameObject.Find("Boiler_Control_Panel_A");
        if (bodyDonor != null)
        {
            var body = Instantiate(bodyDonor, new Vector3(-130.749f, 1.052f, -336.428f), new Quaternion(-.5f,.5f,.5f,.5f), go.transform);
            body.transform.localScale *= .7f;
            var lod = body.GetComponentInChildren<LODGroup>(true);
            if (lod != null) Destroy(lod);
            foreach (var renderer in body.GetComponentsInChildren<Renderer>(true))
                if (renderer.name.IndexOf("SHADOW", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    renderer.enabled = true;
                    renderer.forceRenderingOff = false;
                }
            body.SetActive(true);
        }
        go.SetActive(true);
        return true;
    }

    private void PanelChanged(WorldInteractiveObject panel, EDoorState previous, EDoorState next)
    {
        if (next != EDoorState.Open || _panelPending || string.IsNullOrEmpty(_raid)) return;
        _panelPending = true;
        StartCoroutine(SendOperation());
    }

    private IEnumerator SendOperation()
    {
        var url = "/ember/lab/panel/" + Uri.EscapeDataString(_raid);
        var request = Task.Run(() => JObject.Parse(RequestHandler.GetJson(url)));
        yield return new WaitUntil(() => request.IsCompleted);
        var confirmed = request.Status == TaskStatus.RanToCompletion && (bool?)request.Result["ok"] == true;
        ClientDiagnostics.Record("lab.panel_interaction", new { confirmed });
        if (confirmed) EmergencyLight(PanelPosition + new Vector3(-1f, .5f, 0), 4f);
        else if (_panel != null) _panel.DoorState = EDoorState.Shut;
        _panelPending = confirmed;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        if (_panel != null) _panel.OnDoorStateChanged -= PanelChanged;
        for (var i = _restore.Count - 1; i >= 0; i--) _restore[i]();
    }
}
