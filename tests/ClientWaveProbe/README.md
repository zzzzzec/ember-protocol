# Development-only finite-wave probe

This BepInEx test project is deliberately absent from `pack.json`. Never include it in the Player package.

The plugin requires all guards: installed role `Development`, port `6974`, a `.sptz/ui-qa.json` username beginning `Ember-UI-QA-`, and the live character nickname `EmberUITest`. It rejects all other installations/characters. It makes the dedicated QA actor immune to damage during the test and therefore cannot establish normal combat balance or survival acceptance.

Build with matching game references, then copy only `ClientWaveProbe.dll` into a temporary `BepInEx/plugins/Ember-WaveQA` folder in the stopped Development client. Enter a real raid through the game UI. Read `.sptz/wave-probe-state.json`: wait for `raidReady` and a sealed budget; confirm native actors and `alive + pending` never exceed the cap.

A `.sptz/wave-probe-command.json` object accepts a new unique `id`, an unexpired ISO `expiresUtc`, and either `action: kill_to_two` or `action: kill_all`. Each command runs once and invokes native death only on infected AI. Use it to drain each settled batch, observe the real eight-second breather and native spawn path, then clear the final batch. Confirm the full planned total and no additional actors after clearance. These are synthetic NPC deaths, not player weapon kills or quest acceptance.

Keep raw probe output outside Git. Stop the Development client and remove its exact temporary probe DLL after testing; verify the release package never contains that DLL. Never use this harness against port 6969 or a real player profile.


`action: armor_contract` requires at least two alive generated class-five infected. It calls native `SetShotStatus` and `ApplyShot` with controlled 50-damage, penetration-5/54 shots, and checks chest health, armor wear, helmet top and exposed face. Read `.sptz/armor-contract.json` and require low-penetration blocking, high-penetration damage, reduced durability, `helmetTopBlocks` and `openFaceUnblocked`. This is a synthetic native ballistic contract on real generated actors, **not** a fired projectile, trajectory or normal-combat test. Impact logs label synthetic calls separately.

The one-second snapshot includes individual armor templates/levels/wear, head hit regions, enabled slot renderers, actor positions/velocity and render dimensions. Component/renderer presence is not visual appearance acceptance. A fixed camera FPS sample is not a controlled performance comparison. Native spawn safety may defer a last batch while the QA player stays still; report partial issuance honestly rather than weakening spawn safety to finish a test.
