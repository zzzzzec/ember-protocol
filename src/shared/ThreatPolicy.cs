using System;
using System.Collections.Generic;
using System.Linq;

namespace Codex.ZombieSurvival.Rules;

public sealed class ThreatArchetype
{
    public int Key { get; set; }
    public string Id { get; set; } = "";
    public int MinRank { get; set; } = 1;
    public bool Armed { get; set; }
    public bool Ordinary { get; set; }
    public int Head { get; set; } = 35;
    public int Chest { get; set; } = 100;
    public float Speed { get; set; } = .8f;
    public float SightRange { get; set; } = 30;
    public float SightAngle { get; set; } = 110;
    public float Hearing { get; set; } = .8f;
    public float ReactionSeconds { get; set; } = 1;
    public int ArmorClass { get; set; }
    public int HelmetClass { get; set; }
    public string ArmorTpl { get; set; } = "";
    public string HelmetTpl { get; set; } = "";
    public float MaxFraction { get; set; } = 1;
    public int WearCode { get; set; } = 40;
    public float PlateFraction => .15f + .0055f * WearCode;
    public float SoftFraction => .45f + .003f * WearCode;
    public ThreatArchetype WithWear(int code) { if(code<0||code>99)throw new ArgumentException("Invalid armor wear");var copy=(ThreatArchetype)MemberwiseClone();copy.WearCode=code;return copy; }
    public int RenderCost => 1 + (ArmorTpl.Length > 0 ? 1 : 0) + (HelmetTpl.Length > 0 ? 1 : 0);
    public int Score => ThreatPolicy.Score(this);
}
public sealed class ThreatStrategy
{
    public int Budget { get; set; }
    public int MaxUnitScore { get; set; } = 70;
    public float MinOrdinaryFraction { get; set; } = .2f;
    public float MaxRenderCostPerActor { get; set; } = 2.5f;
    public Dictionary<string, int> Weights { get; set; } = new();
}
public sealed class ThreatConfiguration
{
    public bool Enabled { get; set; }
    public int Revision { get; set; } = 1;
    public List<ThreatArchetype> Archetypes { get; set; } = new();
    public Dictionary<string, ThreatStrategy> Maps { get; set; } = new();
}
public sealed class ThreatPlan
{
    public int Seed { get; set; }
    public int Budget { get; set; }
    public int Spent { get; set; }
    public int RenderCost { get; set; }
    public List<ThreatArchetype> Units { get; set; } = new();
}

// Stable integer PRNG: a logged seed generates the same composition in both runtimes.
public static class ThreatPolicy
{
    private struct Dice
    {
        private uint _state;
        public Dice(int seed) { _state = unchecked((uint)seed); if (_state == 0) _state = 0x9e3779b9; }
        public int Next(int max) { _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5; return (int)(_state % (uint)max); }
    }
    public static int Score(ThreatArchetype a)
    {
        var armorFactor=.45+.55*Math.Sqrt(a.ArmorClass>=3?a.PlateFraction:a.SoftFraction);
        var helmetFactor=.45+.55*Math.Sqrt(a.SoftFraction);
        var result = 10d + Math.Max(0, a.Head - 35) * .12 + Math.Max(0, a.Chest - 100) * .025
            + Math.Max(0, a.Speed - .75) * 30 + Math.Max(0, a.SightRange - 25) * .08
            + Math.Max(0, a.SightAngle - 100) * .02 + Math.Max(0, a.Hearing - .65) * 8
            + Math.Max(0, 1.1 - a.ReactionSeconds) * 6 + a.ArmorClass * 2.5 * armorFactor + a.HelmetClass * 2 * helmetFactor + (a.Armed ? 14 : 0);
        // Crossing a headshot threshold and combining fast movement with protection are costly.
        if (a.Head > 50) result += 8;
        if (a.Speed > .95f && a.ArmorClass >= 3) result += (a.Speed - .95) * a.ArmorClass * 50;
        if (a.Armed && a.ArmorClass >= 3) result += a.ArmorClass*armorFactor;
        return (int)Math.Ceiling(result);
    }
    public static void Validate(ThreatConfiguration c)
    {
        if (c.Revision < 1 || c.Revision > 255 || c.Archetypes.Count == 0 || c.Archetypes.Select(a => a.Key).Distinct().Count() != c.Archetypes.Count)
            throw new ArgumentException("Invalid threat revision or archetype keys");
        foreach (var a in c.Archetypes)
            if (a.Key < 1 || a.Key > 255 || a.WearCode < 0 || a.WearCode > 99 || a.Id.Length == 0 || a.Head < 1 || a.Head > 50 || a.Chest < 50 || a.Chest > 200
                || a.Speed < .6f || a.Speed > 1.15f || a.SightRange < 10 || a.SightRange > 90 || a.SightAngle < 60 || a.SightAngle > 160
                || a.Hearing < .4f || a.Hearing > 1.15f || a.ReactionSeconds < .3f || a.ReactionSeconds > 2.5f
                || a.MaxFraction <= 0 || a.MaxFraction > 1 || a.ArmorClass < 0 || a.ArmorClass > 6 || a.HelmetClass < 0 || a.HelmetClass > 6
                || (a.ArmorClass > 0) != (a.ArmorTpl.Length > 0) || (a.HelmetClass > 0) != (a.HelmetTpl.Length > 0))
                throw new ArgumentException("Invalid threat archetype: " + a.Id);
        foreach (var m in c.Maps.Values)
            if (m.Budget <= 0 || m.MaxUnitScore < 10 || m.MinOrdinaryFraction < 0 || m.MinOrdinaryFraction > 1 || m.MaxRenderCostPerActor < 1 || m.MaxRenderCostPerActor > 3)
                throw new ArgumentException("Invalid threat map strategy");
    }
    public static ThreatPlan Generate(ThreatConfiguration config, string map, int rank, int total, int shooters, int seed, double budgetScale = 1)
    {
        Validate(config);
        if (total < 1 || shooters < 0 || shooters > total || !config.Maps.TryGetValue(map, out var strategy) || budgetScale < 1 || budgetScale > 3)
            throw new ArgumentException("Invalid threat plan request");
        var budget = (int)Math.Round(strategy.Budget * budgetScale);
        var eligible = config.Archetypes.Where(a => a.MinRank <= rank && a.Score <= strategy.MaxUnitScore
            && strategy.Weights.TryGetValue(a.Id, out var weight) && weight > 0).OrderBy(a => a.Key).ToArray();
        var dice=new Dice(seed);
        var variants=new List<ThreatArchetype[]>();
        var units = new List<ThreatArchetype>();
        for (int i = 0; i < total; i++)
        {
            var armed = (i + 1) * shooters / total > i * shooters / total;
            var roll=dice.Next(100);
            var wear=roll<65?dice.Next(37):roll<95?37+dice.Next(27):64+dice.Next(36);
            var choices=eligible.Select(a=>a.WithWear(wear)).ToArray();variants.Add(choices);
            units.Add(choices.Where(a => a.Armed == armed).OrderBy(a => a.Score).First());
        }
        var spent = units.Sum(a => a.Score); var render = units.Sum(a => a.RenderCost);
        if (spent > budget) throw new ArgumentException("Threat budget cannot fund the fixed raid population");
        var counts = units.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Count());
        var ordinary = units.Count(a => a.Ordinary); var minOrdinary = (int)Math.Ceiling((total - shooters) * strategy.MinOrdinaryFraction);
        var maxRender = (int)Math.Floor(total * strategy.MaxRenderCostPerActor);

        // Bounded upgrades. Never retry until a random budget happens to fit.
        for (int step = 0; step < total * eligible.Length; step++)
        {
            var options = new List<(int Index, ThreatArchetype Unit, int Weight)>(); var weightSum = 0;
            for (int i = 0; i < units.Count; i++)
                foreach (var next in variants[i])
                {
                    var old = units[i]; var cost = next.Score - old.Score;
                    if (old.Armed != next.Armed || cost <= 0 || spent + cost > budget || render + next.RenderCost - old.RenderCost > maxRender
                        || counts.GetValueOrDefault(next.Key) >= Math.Max(1, (int)Math.Floor(total * next.MaxFraction))
                        || ordinary + (next.Ordinary ? 1 : 0) - (old.Ordinary ? 1 : 0) < minOrdinary) continue;
                    var weight = strategy.Weights[next.Id]; options.Add((i, next, weight)); weightSum += weight;
                }
            if (options.Count == 0) break;
            var roll = dice.Next(weightSum); var selected = options[0];
            foreach (var option in options) { roll -= option.Weight; if (roll < 0) { selected = option; break; } }
            var previous = units[selected.Index]; counts[previous.Key]--; counts[selected.Unit.Key] = counts.GetValueOrDefault(selected.Unit.Key) + 1;
            ordinary += (selected.Unit.Ordinary ? 1 : 0) - (previous.Ordinary ? 1 : 0);
            spent += selected.Unit.Score - previous.Score; render += selected.Unit.RenderCost - previous.RenderCost; units[selected.Index] = selected.Unit;
        }
        // Spread each type through the role-specific pool, so cached profile order has no large type blocks.
        var spread = new List<ThreatArchetype>();
        foreach (var armed in new[] { false, true })
        {
            var groups = units.Where(a => a.Armed == armed).GroupBy(a => a.Key).OrderBy(g => g.Key).Select(g => (Units: g.ToArray(), Count: g.Count())).ToArray();
            var used = new int[groups.Length]; var size = groups.Sum(g => g.Count);
            for (int i = 0; i < size; i++)
            {
                var best = -1; var deficit = double.MinValue;
                for (int j = 0; j < groups.Length; j++)
                { var d = groups[j].Count * (i + 1d) / size - used[j]; if (used[j] < groups[j].Count && d > deficit) { deficit = d; best = j; } }
                spread.Add(groups[best].Units[used[best]++]);
            }
        }
        return new ThreatPlan { Seed = seed, Budget = budget, Spent = spent, RenderCost = render, Units = spread };
    }
    // Dedicated NPC ID namespace. Preserve the native uniqueness suffix, with no name/weapon/special skill abuse.
    public static string Encode(string nativeId, int revision, int key, int wearCode=40)
    {
        if (nativeId.Length != 24 || revision < 1 || revision > 255 || key < 1 || key > 255 || wearCode < 0 || wearCode > 99) throw new ArgumentException("Invalid threat identity");
        return "7a32" + revision.ToString("x2") + key.ToString("x2") + wearCode.ToString("x2") + nativeId.Substring(10);
    }
    public static bool TryDecode(string id,int revision,out int key)=>TryDecode(id,revision,out key,out _);
    public static bool TryDecode(string id, int revision, out int key,out int wearCode)
    {
        key = 0;wearCode=0;
        return id.Length == 24 && id.StartsWith("7a32" + revision.ToString("x2"), StringComparison.Ordinal)
            && int.TryParse(id.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out key)
            && int.TryParse(id.Substring(8, 2), System.Globalization.NumberStyles.HexNumber, null, out wearCode)&&wearCode<=99;
    }
}

// Retain recognition briefly after losing sight. A fresh, unobstructed
// observation is still required before the game can assign an enemy.
public sealed class ThreatSight
{
    private string _candidate = "";
    private float _readyAt, _last = -100;
    private bool _recognised, _lost;
    public bool Observe(string target, bool visible, float now, float reaction)
    {
        if (!visible)
        {
            _lost = true;
            if (!_recognised) _candidate = "";
            return false;
        }
        var sampleLifetime = _lost ? 1.25f : 3f;
        if (_candidate != target || now - _last > sampleLifetime)
        {
            _candidate = target;
            _readyAt = now + reaction;
        }
        _lost = false;
        _last = now;
        return _recognised = now >= _readyAt;
    }
}
