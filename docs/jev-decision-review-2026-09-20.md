Jev decision review, 20 September 2026

Jev reliably selects executable options, but the observed strategic and combat quality is uneven. The highest-value improvements are to supply the public context that is currently missing, compute simple consequences before asking Jev, narrow each question, and retain its uncertainty signals. A longer general instruction paragraph alone is unlikely to address the strongest failures.

The initial review inspected the read-only aichatproxy database described in `/Users/hm/prj/aichatproxy/docs/proxy-db.md` and the repository at commit `8c6c050`. Request and response bodies were decompressed and blob references resolved; credentials were not included in the review. The findings below describe that baseline. Implementation and a subsequent live replay are recorded at the end.

The database contains 157 Jev requests on September 20, from 08:58:01 to 14:21:39 UTC (16:58:01–22:21:39 Malaysia time). Eight are initial API/selection probes or a connection check. The other 149 use the gameplay request format, including apparent shop and rest-site replay experiments. They span several prompt versions and interrupted runs. They are not 149 independent tests of playing strength, and they do not establish a win rate. Combat handled by the solver or another provider is outside these Jev logs.

| Observation | Result | Interpretation |
| --- | --- | --- |
| HTTP responses | 157/157 returned 200 | The sampled connection worked. |
| Choices in gameplay-shaped requests | 149/149 belonged to the supplied options | Protocol validity is strong; this does not measure strategy. |
| Card reward selections | Took a card in 25/25; skipped none | A concerning tendency, especially given the unsupported Mirage pick below; not proof every addition was wrong. |
| Combat, including hand selections | 35 decisions; median confidence 0.21; 26 below 0.30 | Many decisions had diffuse option distributions. Thresholds still need calibration. |
| Request size and latency | Median 1,750 input tokens and 1.382 seconds across the 149 requests | The observed problem is not excessive context length or API failure. |
| Latest continuation, IDs 47505–47526 | 18 requests, no combat decisions | Includes successful rest choices and the remaining reward-selection problems. |

**The strongest observed problems and their fixes**

1. **Give Jev route and boss context outside the map screen.** This is a current integration gap, not speculation about model ability. None of the non-map requests contains a `[Map]` section. The live adapter populates `map` only in branches that report the map screen, while the Jev renderer can only forward it if present. Consequently a rest, shop, or reward decision is told to consider upcoming fights without receiving the visible route or boss. The `plan` field is also empty in every gameplay-shaped request, so it does not recover this information.

   Build a public run-context section independently of the active screen: known boss, current map position, reachable paths, distance to recovery and shops, and mandatory versus optional elites. At rest sites, include useful upgrade previews before choosing Rest versus Smith; currently those previews arrive only after committing to Smith. Keep unknown encounters unknown. Link this context to the current run/act so a cached map cannot leak across runs.

   Relevant code: [snapshot map branch](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Game/GameBindings.Snapshot.cs:212), [conditional Jev map rendering](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/GameState.Jev.cs:45), [upgrade preview rendering](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/GameState.Jev.cs:21).

2. **Treat taking a reward as a comparison against keeping the deck unchanged.** In log **47517**, Jev chose Mirage over Prepared, Strangle, and Skip. Its own state described Mirage as gaining Block from enemy Poison, but the current deck, relics, and held potions supplied no Poison source. The deck already had Infinite Blades and Blade Dance, making Strangle a concrete alternative worth comparing. Mirage's main dependency was unsupported. This does not mean every conditional card should be forbidden, or that Dexterity could never give it value.

   In **47522**, Jev chose The Hunt with confidence **0.04**: the distribution was approximately Hunt 0.29, Adrenaline 0.27, Skip 0.25, Corrosive Wave 0.19. That is an uncertain choice, not a strong verdict that The Hunt beats the other cards. Adrenaline offers immediate energy and draw; Corrosive Wave supplies the Poison enabler the deck had just lacked. Both deserved explicit comparison. The existing Prayer Wheel also reduces the urgency of obtaining still more card rewards through The Hunt.

   Supply a compact deck assessment with actual enablers, consumers, draw/discard sources, energy demands, and the next known challenge. Distinguish a card that mentions Poison from one that applies Poison. Use the offered card's marginal benefit and draw dilution, with Skip as an explicit baseline. The current card-reward branch only removes potion-discard options and otherwise uses the broad default question.

   Relevant code: [reward options](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/JevStrategy.cs:93), [default strategy question](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/JevClient.cs:16).

3. **Do not rely on one broad choice question to evaluate combat sequencing.** In **47324**, Jev ended round 1 against Mecha Knight with **5 energy, 0 Block, and an incoming 30-damage attack**, despite having Defend, upgraded Defend, Taunt, an energy-generating card, and potions available. The next request, **47325**, confirms HP fell from **93 to 63**. This is a clear avoidable loss, unlike late-fight states where death might already be unavoidable.

   In **47327**, it used Fortifier while at 0 Block. Reptile Trinket did grant 3 temporary Strength, so calling the potion completely useless would be incorrect. However, playing a Block card first would preserve that Strength trigger while also benefiting from Fortifier's multiplier. This is a sequencing problem that a potion's name alone does not express.

   Supply explicit zero values and public effect summaries: current Block, expected damage if ending now, remaining energy, known end-turn effects, and action-specific damage/block/resource changes. For this example, `end_turn: lose 30 HP, leave 5 energy unused` is more informative than `end_turn`. Compute those facts from game semantics where supported; mark unknown interactions rather than presenting a simplistic damage subtraction as exact. Keep beneficial end-turn exceptions such as Pael's Tears, Orichalcum, retaliation, and deliberate waiting in the evaluation.

   Keep the combat solver as the first choice when available. For unsupported or refused fights, uncertain consequential decisions should receive a bounded fallback to the regular reasoning model or a supported local search. These logs do not demonstrate that Jev alone is a reliable combat planner.

   Relevant code: [combat question](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/JevClient.cs:19), [zero Block currently omitted](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/GameState.Brief.cs:315), [solver priority](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/BotRuntime.cs:385).

4. **Preserve uncertainty and use it to route difficult choices.** Jev returns both `confidence` and the complete `probabilities` map. `JevClient.Decide` validates and returns the selected ID but retains only model identity and token usage in `evaluations`. The local runtime trace therefore loses the most useful signals for detecting and reviewing weak choices. The Mecha Knight end-turn decision had confidence **0.10**; the Mirage pick had **0.18**.

   Record the chosen option, distribution, confidence, prompt version, model version, and the outcome of executing it. Use confidence, the leading-option margin, and concrete consequence checks together to decide whether to escalate. Calibrate by screen type; 0.30 is a useful analysis bin here, not a validated production cutoff. Confidence describes concentration over choices, not the run's chance of winning. [TypeSafe explains the distinction and recommends domain-specific thresholds](https://docs.typesafe.ai/confidence).

   Two replies, **47326** and **47338**, name a choice whose reported probability is 0.01 below another option. The [API reference describes choice as the highest-probability option](https://docs.typesafe.ai/api). Preserve these replies for a compatibility diagnostic; this small discrepancy is insufficient to identify whether rounding, the gateway, or the upstream service is responsible. Automatically replacing every answer with a locally computed argmax is not an established fix.

   Relevant code: [answer handling and discarded distribution](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/JevClient.cs:67), [runtime Jev trace](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/BotRuntime.cs:444).

5. **Describe consequences in each option, and make each question screen-specific.** Most criteria are labels such as `take Mirage[0]`, `select Infinite Blades[13]`, or `map Monster[0]`. Jev must join these to separate state sections and then reconstruct the strategic comparison. Upgrade choices also share the general strategy question, even though their task is specifically to compare the *difference* an upgrade makes. At **47509**, Infinite Blades' upgrade adds Innate rather than increasing Shiv output; showing that delta next to the option makes the tradeoff explicit.

   Use comparable fields per criterion: immediate effect, lasting effect, cost, dependencies, and relevant tradeoff. For map options, summarize reachable recovery, shops, and elite exposure alongside the node coordinate. For upgrade choices, include old versus new text and cost together. Keep exact action IDs and the existing freshness checks.

   Remove standalone potion-discard choices when there is no relevant reason to discard on that screen. Do not ban discards globally: a visible effect that produces potions at combat entry can justify making space. Similarly, deduplicate only genuinely equivalent options; identical-looking cards can have different upgrades or enchantments.

   Normalize energy icon filenames into readable quantities and fix doubled upgrade suffixes such as `Defend++`. `BuildCardInfo` already obtains a display title, while `CardName` appends another `+`. Separate permanent card rules from combat-adjusted previews; the same card can currently appear as several unlabelled rules because complete rendered strings differ between piles.

   Relevant code: [criterion labels](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/JevClient.cs:51), [rule deduplication](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/GameState.Jev.cs:25), [card display formatting](/Users/hm/prj/games/play-sts2/mod/SpireBuddy/Runtime/GameState.Brief.cs:408).

**Existing changes that already help**

The earlier mistakes should not all be counted against the current implementation. Reward sequencing is now handled locally, addressing older requests such as **47346** and **47373**, where Jev selected Proceed while rewards remained.

The shop comparison **47418 → 47419** uses the same shop state: the old question leaves with 362 gold; the revised budget-aware question chooses card removal for 100 gold. This supports making options concrete, but one replay does not prove removal was the optimal purchase or that all shop decisions are fixed.

The full-health rest comparison is stronger. **47484** selected Rest at 70/70 HP. **47492–47494** used the revised question and selected Smith; the later gameplay requests **47505** and **47508** also selected Smith at full HP. At **47519**, the updated question selected Rest at 36/70 HP. The useful change was the explicit consequence, “restore 0 HP,” coupled with the focused rest question. These fixes are already present in the repository.

**Concrete prompt and question candidates**

For card rewards, a replacement question to test:

> Which option most improves this existing deck compared with adding no card? Use the current deck, relics, resources, and upcoming public challenges. Compare immediate usefulness, supported synergies, energy demand, and the cost of diluting future draws. Treat Skip as keeping the deck unchanged. Do not assume future cards or relics will supply missing dependencies. Honor the latest player instructions.

For upgrading:

> Which available upgrade provides the largest useful improvement to this deck before its upcoming challenges? Compare each card's before/after effect and cost, how often it will be drawn and played, and current synergies. Evaluate the benefit of the upgrade itself. Treat Innate as changing opening-hand composition, with both its benefit and opportunity cost. Honor the latest player instructions.

For map navigation, after adding route summaries:

> Which offered route best balances rewards and survival for the current deck and resources? Compare mandatory fights before recovery, optional elites, reachable shops and spending needs, and the known boss. Use only visible topology; unknown rooms remain unknown. Honor the latest player instructions.

For combat, after adding verified consequence summaries:

> Which next action best improves the achievable result of this turn? First compare survival against the visible intents, then useful damage and setup, resource use, and next-turn position. Use the supplied action consequences and current powers. End turn when further actions offer no net benefit or ending has a specific advantage. Re-evaluate after draws or other new information. Honor the latest player instructions.

For difficult reward decisions, test independent typed questions alongside the final choice: `requires_missing_enabler` (Noul), `near_term_survival_gain` (Score), `long_term_gain` (Score), and `draw_consistency_cost` (Score), each attached to one candidate. Define short, concrete scoring levels. Compute simple facts directly instead of asking Jev to count known cards or subtract HP. Inspect these signals before choosing composition weights; they are not automatically calibrated utilities.

TypeSafe's documentation specifically recommends narrow questions, structured option descriptions, and composition in code. Questions in one request are evaluated independently: an auxiliary synergy answer will not automatically influence the action answer in that same call. Use code to combine them, or make a second bounded choice call with the derived results. [TypeSafe workflow guidance](https://docs.typesafe.ai/concepts/how-to-build-with-system-one).

A compact strategy record would also help preserve intent between decisions. Track supported deck strengths, unmet needs, the next public threat, and recent fight HP/potion costs. Refresh it after major rewards, shops, and fights. At present the Jev branch keeps `agent.Plan` unchanged, clears its inbox, and sends only four recent action labels; those labels do not explain whether a fight was easy or nearly fatal. Facts should be updated from outcomes, and any model-generated assessment should remain revisable.

**Implementation and validation order**

1. Preserve confidence/distributions and add prompt/model version metadata, so subsequent changes can be measured.
2. Fix cross-screen public run context; include recovery and boss information and useful upgrade previews at rest sites.
3. Add reward and upgrade questions, explicit option consequences, and readable zero/resource values.
4. Add combat consequence checks and a bounded uncertain-decision fallback, then evaluate any multi-question scoring design.

Use saved snapshots as an offline regression set. Include the avoidable 30-damage end turn (**47324**), Fortifier ordering with its Strength-trigger exception (**47327**), unsupported Mirage (**47517**), near-tied rare reward (**47522**), and the rest/shop comparison pairs. Label sets of acceptable actions where several choices are defensible, rather than declaring one card universally correct.

Compare the old payload, added facts only, new question only, and their combination while fixing the model version. Keep a separate set of unseen snapshots and reorder equivalent choices to check for position sensitivity. Measure clear decision violations, acceptable-choice rate, unnecessary additions/discards, HP loss where outcomes are observable, latency, and fallback frequency. A higher confidence value alone is not evidence of better play. Test full runs only after the snapshot cases improve, and distinguish Jev decisions from solver-controlled combat when interpreting outcomes.

**Implemented in 1.2.4**

The adapter now supplies current public map/boss context on other run screens and
before/after upgrade previews at rest sites. Jev receives focused questions for
rewards, upgrades, events, maps and bundles/relics, plus structured options containing
card rules/costs, upgrade changes, target facts, potion details and end-turn exposure.
Deck cost counts and explicitly incomplete support scans supplement the exact rules.
Energy icons and upgrade suffixes are normalized; permanent rules and combat
previews are distinguished. Last-fight net HP/held-potion counts and recent HP/gold
changes preserve observed outcomes across decisions, including solver fights.

Full choice answers, prompt/model identity and usage are retained in traces. A
single isolated review through the configured Buddy model was initially enabled
by default in 1.2.4. Version 1.2.5 makes it opt-in, with the in-game toggle off by
default. Initial triggers are confidence below 0.20 for strategy or
0.30 for combat, leading probability margin at most 0.05, ending with unused energy
and playable cards while visible attacks exceed Block, and Fortifier at zero Block.
These are routing heuristics, not calibrated accuracy cutoffs. Reviews can retain
end-turn and potion-triggered benefits; they choose from current legal IDs and pass
the normal stop, guidance and freshness checks. Malformed reviews execute nothing.

The reported reward loop had a separate controller cause: skipping a card offer
leaves its loot button enabled. The controller now remembers each handled reward's
object identity after a settled pick/skip. It opens each distinct offer once, handles
remaining loot, then proceeds, even when two offers have identical labels/cards.
Reward IDs also identify an offer already open when automation starts. No-op clicks
do not count as completed selections; tracking persists across Stop/Play and resets
with the run location. Tests cover all take/skip combinations for two offers,
reindexed rows, other loot, and starting within the selection screen.

Independent multi-question utility scoring remains an experiment, rather than an
extra production decision layer. Its signals are not calibrated or automatically
composed. The bounded reasoning review handles difficult choices without inventing
utility weights; a broader held-out evaluation is still needed before claiming
improved playing strength.

**Live replay after implementation**

Ten read-only inference calls replayed five saved decisions through the configured
Jev proxy: one original request and one with revised questions/structured criteria
per state. All resolved to `typesafe/jev-1.13`, although the configured alias remained
`jev-latest`; all returned legal choices. The reconstructed variants also normalized
energy/upgrade text, made zero Block explicit in combat, and removed irrelevant
upgrade-screen potion discards. They did not invent missing historic map context or
run the entire new adapter, so this is a small combined-payload smoke comparison,
not a controlled ablation or held-out benchmark.

| Log | Original replay | Revised replay | What this supports |
| --- | --- | --- | --- |
| 47517 | Mirage, confidence 0.17 | Strangle, 0.51 | Supported immediate synergy can beat the unsupported Poison payoff. |
| 47522 | The Hunt, 0.04 | Adrenaline, 0.20 | Immediate energy/draw becomes competitive; the 0.03 leading margin still requests review. |
| 47509 | Infinite Blades upgrade, 0.39 | Same upgrade, 0.56 | No established improvement; higher confidence alone proves nothing. |
| 47324 | End turn, 0.16 | Fortifier at zero Block, 0.16 | A different choice still needs concrete consequence review. |
| 47327 | Fortifier at zero Block, 0.14 | Same potion, 0.32 | A confidence threshold alone would miss this sequencing issue. |

The five revised Jev calls took 0.84–1.46 seconds each. Two additional isolated
reviews with the configured `gpt-6-astra` model selected Dark Embrace in 47324 and
Shrug It Off in 47327, instead of the zero-Block Fortifier proposal. Both were legal
and took 28.48 and 18.78 seconds respectively. These are next-action checks, not
executed fights or proof of the best full sequence; they demonstrate the added
latency as well as why review should be selective. No game action was executed by
any replay.

Validation completed with the full `SpireBuddy.Tests` native runtime suite, a final
targeted `--jev` run, the multi-reward regressions, and a Release build against the
installed game assemblies. The game build retains five existing nullable warnings
in `GameBindings.Wiki.cs`; it has no errors. Version 1.2.4 was installed with the
standard installer, and SHA-256 checks confirmed all four installed files match the
built/source files. The game must be restarted to load the new assembly; no fresh
full run or in-game verification after restarting was performed.
