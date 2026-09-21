# Jev missed-lethal fix, 1.2.6

The recent proxy requests show a concrete combat loss caused by missing a
two-card kill. This review uses the documented read-only proxy database access;
request/response bodies were decoded without exporting authentication headers.
The relevant gameplay requests are from 2026-09-20 UTC, reviewed on September 21
in the local timezone.

## Evidence

| Request | Public position | Jev action | Consequence |
| --- | --- | --- | --- |
| 47705 | Ironclad at 3 HP; Bygone Effigy at 18 HP, Vulnerable 1, Slow 0; 3 energy and two Strikes | Dexterity Potion | Misses an available 9 + 9 damage kill and spends a potion. |
| 47707 | Same enemy at 18 HP, now Slow 10; 2 energy, 7 Block and two Strikes | Defend | Gives up the remaining 9 + 10 damage kill. |
| 47708–47709 | 1 energy becomes 0; Block increases to 21 against a 25-damage attack | Defend, then End Turn | The visible attack exceeds Block by 4 with only 3 HP remaining. |
| 47665 | Shrinker Beetle at 41 HP, intending a debuff; Strike, Cruelty and three Defends in hand | Defend | Spends energy on Block without a visible defensive need or a card/relic payoff. |

The installed game's `SlowPower`, `VulnerablePower`, `StrikeIronclad`, damage
hooks and attack command confirm the arithmetic. Slow increases **after** a
card resolves. A Strike against Vulnerable deals floor(6 × 1.5 × Slow factor):
9 at Slow 0 and Slow 10, then 10 at Slow 20. The two Strikes therefore kill in
both reported positions before the enemy can attack.

## Causes and changes

The previous combat question prioritized surviving the visible intent before
damage and setup. It did not explicitly compare a whole affordable kill
sequence against spending that energy on Block. The new question checks lethal
first, then killing individual attackers, defense and later value. It explicitly
distinguishes beneficial setup from unnecessary Block against a non-attacking
enemy. Combat no longer carries route traversal and deck-building scan output.

The adapter called `GetDescriptionForPile`, which formats cached dynamic preview
values without recalculating them. Logs contain inconsistent values for the
same card across hand and pile descriptions. Descriptions now refresh native
preview calculations and restore the original presentation fields afterward.
Every attack also has target-specific description/damage-variable previews,
including current Vulnerable/Slow and other native modifiers. These are labelled
per-hit values before Block and HP-loss/death effects, not a universal damage
simulator. The actual target preview accompanies its legal action criterion.

A bounded local attack search prevents supported, provable kills from depending
on Jev's arithmetic. The initial scope is the five starting Strikes, Bash, Twin
Strike, Unrelenting and Perfected Strike. It models Block, damage rounding,
Vulnerable application, Slow after the card, Strength/Weak, Vigor consumed after
the first attack, one-use free attacks and energy/stars. Current calculations must
agree with the native target previews. It searches at most eight cards, fifteen
candidate attacks, four enemies and 20,000 nodes. Exhausting the search yields
no proof; it does not claim that no lethal exists.

The adapter checks all combat hook listeners, including cards in other piles,
monster callbacks, modifiers and mod subscribers. Unknown hooks, missing/hidden
powers, melted relics, multiplayer/pets and modified attack cards prevent a proof.
Runtime validation independently rejects unsupported visible powers. This avoids
claiming lethal through unmodelled retaliation, Intangible, reactive Block or
revival. Random attacks, draws and potion combinations remain Jev decisions.

A proof selects only the first currently legal action. Existing snapshot,
guidance, cancellation and action-settlement checks still apply; the controller
recomputes from the next snapshot rather than executing stored hand indexes.
The configured Combat Solver keeps priority. Local decisions are logged as
`jev_lethal` with proof steps; no Buddy model request is made. Optional Buddy
review remains off by default.

Energy normalization also now binds a spaced number to its icon. Unrelenting's
“costs 0 [energy_icon.png]” becomes “costs 0 Energy”, instead of the misleading
“costs 0 1 Energy”.

## Live inference replay

Six read-only calls compared the original payloads with the revised combat
question, reduced combat context and reconstructed target previews. Each call
resolved to `typesafe/jev-1.13` via the configured proxy. No game actions executed.

| Request | Original payload replay | Revised payload replay |
| --- | --- | --- |
| 47705 | Dexterity Potion, confidence 0.34 | Strike, confidence 0.24 |
| 47707 | Defend, confidence 0.24 | Strike, confidence 0.34 |
| 47665 | Defend, confidence 0.22 | Strike, confidence 0.42 |

Calls took 0.98–1.63 seconds. This is a small regression smoke comparison, not a
held-out win-rate study or an isolated prompt ablation. In particular, higher
confidence is not necessary for the better action. The local proof covers the
first two positions independently of Jev's answer and confidence. The third
still depends on the model; taking Strike first does not prove optimal play for
the remainder of that fight.

## Validation

`--jev-combat` covers both captured lethal positions, insufficient resources,
native-preview disagreement, consumed Vigor/free attacks, Bash/Unrelenting
ordering, Slow timing for multi-hit attacks, Block, Weak, star costs, illegal
cards, target identity, multiple enemies and unsupported mechanics.

`--jev` also checks the real controller path: both Strikes execute with fresh
hand indexes and no model call, a changed unsupported state between the attacks
returns control to Jev, proof events are logged, and Stop during the action
preview prevents execution. Existing provider routing, solver priority,
freshness, steering, review defaults and error handling remain covered.

The full native runtime suite and a final targeted `--jev` run passed. The Release
build succeeded against the installed game assemblies. SHA-256 checks confirmed
that all four installed and Workshop package files match their build/source files
and that both manifests report 1.2.6. Restart the game to load the new assembly;
no restarted in-game run was executed to measure playing strength beyond these
regression cases.
