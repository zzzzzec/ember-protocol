using System;
using System.Collections.Generic;

namespace Codex.ZombieSurvival.Rules;

internal sealed class ReloadPractice
{
    private readonly Dictionary<string, int> _firedRounds = new(StringComparer.Ordinal);

    internal bool HasRounds(string weaponId) => _firedRounds.ContainsKey(weaponId);

    internal void Fired(string weaponId)
        => _firedRounds[weaponId] = _firedRounds.TryGetValue(weaponId, out var rounds) ? rounds + 1 : 1;

    internal int Finish(string weaponId, bool succeeded, bool loadedAmmunition)
        => succeeded && loadedAmmunition && _firedRounds.Remove(weaponId, out var rounds) ? rounds : 0;
}
