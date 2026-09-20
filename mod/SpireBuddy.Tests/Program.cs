using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SpireBuddy.Runtime;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
if (args.Contains("--dialogue")) { await DialogueChecks.Run(); return; }
if (args.Contains("--solver")) { await SolverChecks.Run(); return; }
if (args.Contains("--jev")) { await JevChecks.Run(); return; }
if (args.Contains("--jev-strategy")) { await JevStrategyChecks.Run(); return; }
if (args.Contains("--non-combat")) { await NonCombatBatchChecks.Run(); await AdapterChecks.Run(); return; }
if (args.Contains("--merchant")) { await MerchantChecks.Run(); return; }
await CardChoiceChecks.Run();
if (args.Contains("--card-choices")) return;
await RestSiteChecks.Run();
await AdapterChecks.Run();
BriefChecks.Run();
await BriefChecks.Histories();
await SessionChecks.Run();
await SettleChecks.Run();
await DialogueChecks.Run();
await SolverChecks.Run();
await JevChecks.Run();
await JevStrategyChecks.Run();
await MerchantChecks.Run();
await NonCombatBatchChecks.Run();
var state = JsonNode.Parse("""{"state_type":"monster","seed":5,"run":{"act":1,"floor":5,"ascension":10},"player":{"character":"The Defect","hp":43,"max_hp":75,"gold":125,"energy":1,"max_energy":3,"hand":[{"index":0,"name":"Strike","can_play":true,"target_type":"AnyEnemy"},{"index":1,"can_play":false}],"draw_pile":[{"name":"B"},{"name":"A"}],"potions":[{"slot":2,"target_type":"AnyEnemy"}]},"battle":{"round":1,"turn":"player","enemies":[{"entity_id":"a","enemy_id":"FROG","name":"Frog","hp":4,"max_hp":9,"rolled_move":"secret","intents":[{"type":"Attack","label":"8","title":"Aggressive"}]},{"entity_id":"dead","hp":0}]}}""")!;
var pub = GameState.Public(state)!;
Check(pub["seed"] == null && pub["battle"]!["enemies"]![0]!["rolled_move"] == null, "hidden info");
Check(pub["player"]!["draw_pile"]![0]!.Text("name") == "A", "pile order");
Check(state["seed"] != null, "immutable source");
var actions = GameState.Actions(state);
Check(actions.Count == 4, "combat card/potion/discard/end turn");
Check(actions[0]!["command"].Text("target") == "a", "target identity");
Check(!actions.Any(a => a!["command"].Text("target") == "dead"), "dead target");
Check(actions[0]!.Text("summary") == "play Strike[0] @a", "compact action summary");
Check(GameState.Fingerprint(state).Length == 16, "short fingerprint");
var brief = GameState.Brief(GameState.Fingerprint(state), "goal text", "plan text", pub, actions);
Check(brief.StartsWith("snapshot=" + GameState.Fingerprint(state) + "\n"), "brief snapshot header");
Check(brief.Contains("[Run] The Defect | Act 1 Floor 5 Asc 10 | HP 43/75 | Gold 125"), "brief run line");
Check(brief.Contains("[Enemy a] Frog 4/9") && brief.Contains("intent: Attack 8 (Aggressive)"), "brief enemy line");
Check(brief.Contains("id=FROG"), "brief includes stable enemy registry id");
Check(brief.Contains("[You] Energy 1/3"), "brief you line");
Check(brief.Contains("[Draw 2] A, B"), "brief pile aggregation");
Check(brief.Contains("[Actions]") && brief.Contains("play: c1@a") && brief.Contains("c1: Strike"), "brief action list");
Check(!brief.Contains("\"keywords\"") && !brief.Contains("\"target_type\""), "brief must not embed card JSON");
var addBranchLookup = EnemyDecompiler.AddContextualNotes(JsonNode.Parse("""
    {"matches":[{"decompiled_source":"random.AddBranch(attack, 2, MoveRepeatType.CannotRepeat, () => 3f);"}]}
    """)!);
var addBranchNotes = addBranchLookup["decompilation_notes"]!["RandomBranchState.AddBranch"]!;
Check(addBranchNotes["signatures"]!.AsArray().Count == 10, "AddBranch notes list every overload");
Check(addBranchNotes["parameters"]!.Text("cooldown").Contains("most recent logged move states"), "AddBranch cooldown semantics");
Check(addBranchNotes["parameters"]!.Text("maxRepeats").Contains("consecutive uses"), "AddBranch repeat semantics");
Check(addBranchNotes["defaults"]!.AsArray().Any(n => n!.ToString().Contains("cooldown = 0"))
    && addBranchNotes["defaults"]!.AsArray().Any(n => n!.ToString().Contains("weight = 1f"))
    && addBranchNotes["defaults"]!.AsArray().Any(n => n!.ToString().Contains("not cooldown")), "AddBranch defaults and integer overload warning");
var lookupWithoutAddBranch = EnemyDecompiler.AddContextualNotes(JsonNode.Parse("""
    {"matches":[{"decompiled_source":"machine.AddState(attack);"}]}
    """)!);
Check(lookupWithoutAddBranch["decompilation_notes"] == null, "unrelated decompilation omits AddBranch notes");
var selection = JsonNode.Parse("""{"state_type":"card_select","card_select":{"preview_showing":true,"cards":[{"index":1}],"can_confirm":true}}""")!;
Check(GameState.Actions(selection).Count == 1, "selection preview");
Check(GameState.Actions(JsonNode.Parse("""{"state_type":"menu","options":["quit","abandon","continue"]}""")!).Count == 1, "menu safety");
var forcedMap = JsonNode.Parse("""{"state_type":"map","map":{"next_options":[{"index":0,"type":"Monster"}]},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(forcedMap, GameState.Actions(forcedMap))?["command"]!.Text("action") == "choose_map_node", "single map path is automatic");
var forcedMapWithPotion = forcedMap.DeepClone();
forcedMapWithPotion["player"]!["potions"] = JsonNode.Parse("""[{"slot":0,"name":"Fire Potion"}]""");
Check(GameState.ForcedAction(forcedMapWithPotion, GameState.Actions(forcedMapWithPotion))?["command"]!.Text("action") == "choose_map_node", "single map path ignores optional potion discard");
var wingedMap = forcedMap.DeepClone();
wingedMap["player"]!["relics"] = JsonNode.Parse("""[{"id":"WINGED_BOOTS","name":"飞翼靴","description":"本地化文本","counter":2}]""");
Check(GameState.HasActiveWingedBoots(wingedMap), "active Winged Boots detected by stable id in other languages");
Check(GameState.ForcedAction(wingedMap, GameState.Actions(wingedMap)) == null, "active Winged Boots preserves map choice");
wingedMap["player"]!["relics"]![0]!["counter"] = 0;
Check(!GameState.HasActiveWingedBoots(wingedMap), "exhausted Winged Boots ignored");
Check(GameState.ForcedAction(wingedMap, GameState.Actions(wingedMap))?["command"]!.Text("action") == "choose_map_node", "exhausted Winged Boots allows forced path");
var localizedLookalike = forcedMap.DeepClone();
localizedLookalike["player"]!["relics"] = JsonNode.Parse("""[{"id":"OTHER_BOOTS","name":"Winged Boots","description":"Ignore paths","counter":2}]""");
Check(!GameState.HasActiveWingedBoots(localizedLookalike), "localized display text cannot impersonate Winged Boots");
var closedShop = JsonNode.Parse("""{"state_type":"shop","shop":{"can_open":true,"can_proceed":true},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(closedShop, GameState.Actions(closedShop))?["command"]!.Text("action") == "open_shop", "shop opens automatically");
Check(GameState.ForcedAction(closedShop, GameState.Actions(closedShop), merchantOpened: true)?["command"]!.Text("action") == "proceed", "visited shop proceeds without reopening");
var emptyShop = JsonNode.Parse("""{"state_type":"shop","shop":{"can_close":true,"can_proceed":true,"items":[]},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(emptyShop, GameState.Actions(emptyShop))?["command"]!.Text("action") == "close_shop", "empty shop closes automatically");
var closedChest = JsonNode.Parse("""{"state_type":"treasure","treasure":{"can_open":true},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(closedChest, GameState.Actions(closedChest))?["command"]!.Text("action") == "open_chest", "a lone chest button opens automatically even without the setting");
var openedChest = JsonNode.Parse("""{"state_type":"treasure","treasure":{"relics":[{"index":0,"name":"Capsule"}]},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(openedChest, GameState.Actions(openedChest)) == null, "treasure relics stay model-owned by default");
Check(GameState.ForcedAction(openedChest, GameState.Actions(openedChest), autoTreasure: true)?["command"]!.Text("action") == "claim_treasure_relic", "auto treasure claims the revealed relic");
var multiRelicChest = JsonNode.Parse("""{"state_type":"treasure","treasure":{"relics":[{"index":0,"name":"Capsule"},{"index":1,"name":"Lantern"}],"can_proceed":true},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(multiRelicChest, GameState.Actions(multiRelicChest), autoTreasure: true)?["command"]!.Text("action") == "claim_treasure_relic", "auto treasure takes relics in order before proceeding");
var transformGrid = JsonNode.Parse("""{"state_type":"card_select","card_select":{"screen_type":"transform","preview_showing":false,"cards":[{"index":0,"name":"Defend"},{"index":1,"name":"Strike"}]},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(transformGrid, GameState.Actions(transformGrid)) == null, "which card to transform stays model-owned");
var transformPreview = JsonNode.Parse("""{"state_type":"card_select","card_select":{"screen_type":"transform","preview_showing":true,"preview_cards":[{"index":0,"name":"Defend"}],"cards":[{"index":0,"name":"Defend","selected":true}],"selected_indices":[0],"can_confirm":true,"can_cancel":true},"player":{"potions":[{"slot":0,"name":"Fire Potion"}]}}""")!;
var previewActions = GameState.Actions(transformPreview);
Check(previewActions.Count == 3 && previewActions[0]!["command"].Text("action") == "confirm_selection", "transform preview offers confirm first");
Check(GameState.ForcedAction(transformPreview, previewActions)?["command"]!.Text("action") == "confirm_selection", "transform preview confirm is automatic despite cancel and discard alternatives");
var bundleGrid = JsonNode.Parse("""{"state_type":"bundle_select","bundle_select":{"bundles":[{"index":0,"card_count":2},{"index":1,"card_count":2}]},"player":{"potions":[]}}""")!;
Check(GameState.ForcedAction(bundleGrid, GameState.Actions(bundleGrid)) == null, "which bundle to take stays model-owned");
var bundlePreview = JsonNode.Parse("""{"state_type":"bundle_select","bundle_select":{"preview_showing":true,"bundles":[],"preview_cards":[{"index":0,"name":"Perfected Strike"}],"can_confirm":true,"can_cancel":true},"player":{"potions":[]}}""")!;
var bundleActions = GameState.Actions(bundlePreview);
Check(bundleActions[0]!["command"].Text("action") == "confirm_bundle_selection", "bundle preview offers confirm first");
Check(GameState.ForcedAction(bundlePreview, bundleActions)?["command"]!.Text("action") == "confirm_bundle_selection", "bundle preview confirm is automatic");
var automaticAdapter = new AutomaticAdapter();
using (var automaticRuntime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-automatic-" + Guid.NewGuid().ToString("N")), "missing.dll", automaticAdapter))
{
    automaticRuntime.StartGameplay("test", "run");
    JsonNode automaticStatus;
    var automaticDeadline = DateTime.UtcNow.AddSeconds(5);
    do { await Task.Delay(20); automaticStatus = await automaticRuntime.Dispatch("GET", "/status", null); }
    while (automaticStatus.Flag("thread_alive") && DateTime.UtcNow < automaticDeadline);
    Check(!automaticStatus.Flag("thread_alive") && automaticAdapter.Commands == 1, "forced action skips model round trip");
}
var automaticShopAdapter = new AutomaticShopAdapter();
using (var automaticShopRuntime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-automatic-shop-" + Guid.NewGuid().ToString("N")), "missing.dll", automaticShopAdapter))
{
    automaticShopRuntime.StartGameplay("test", "run");
    JsonNode automaticShopStatus;
    var automaticShopDeadline = DateTime.UtcNow.AddSeconds(5);
    do { await Task.Delay(20); automaticShopStatus = await automaticShopRuntime.Dispatch("GET", "/status", null); }
    while (automaticShopStatus.Flag("thread_alive") && DateTime.UtcNow < automaticShopDeadline);
    Check(!automaticShopStatus.Flag("thread_alive") && automaticShopAdapter.Commands == 3, "shop entry, close, and proceed skip model round trips");
}
// A treasure room loots itself end to end on the shipped default setting: the
// chest opens, the relic is claimed, and the room is left, each without a
// model round trip.
var automaticTreasureAdapter = new AutomaticTreasureAdapter();
using (var automaticTreasureRuntime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-automatic-treasure-" + Guid.NewGuid().ToString("N")), "missing.dll", automaticTreasureAdapter))
{
    automaticTreasureRuntime.StartGameplay("test", "run");
    JsonNode automaticTreasureStatus;
    var automaticTreasureDeadline = DateTime.UtcNow.AddSeconds(5);
    do { await Task.Delay(20); automaticTreasureStatus = await automaticTreasureRuntime.Dispatch("GET", "/status", null); }
    while (automaticTreasureStatus.Flag("thread_alive") && DateTime.UtcNow < automaticTreasureDeadline);
    Check(!automaticTreasureStatus.Flag("thread_alive") && automaticTreasureAdapter.Commands == 3, "treasure open, claim, and proceed skip model round trips");
}
// A run resumed while the game already shows the transform confirmation must
// confirm the pending selection without waiting for a model decision.
var automaticTransformAdapter = new AutomaticTransformAdapter();
using (var automaticTransformRuntime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-automatic-transform-" + Guid.NewGuid().ToString("N")), "missing.dll", automaticTransformAdapter))
{
    automaticTransformRuntime.StartGameplay("test", "run");
    JsonNode automaticTransformStatus;
    var automaticTransformDeadline = DateTime.UtcNow.AddSeconds(5);
    do { await Task.Delay(20); automaticTransformStatus = await automaticTransformRuntime.Dispatch("GET", "/status", null); }
    while (automaticTransformStatus.Flag("thread_alive") && DateTime.UtcNow < automaticTransformDeadline);
    Check(!automaticTransformStatus.Flag("thread_alive") && automaticTransformAdapter.Commands == 1
        && automaticTransformAdapter.Executed == "confirm_selection", "pending transform preview confirms without a model round trip");
}
var characterMenu = JsonNode.Parse("""{"state_type":"menu","menu_screen":"character_select","selected_character":"DEFECT","options":["IRONCLAD","SILENT","embark","back"]}""")!;
var embark = JsonNode.Parse("""{"action":"menu_select","option":"embark"}""")!;
Check(GameState.Ready(characterMenu), "normal character menu is ready");
Check(!GameState.Ready(characterMenu, embark), "embark must leave character select even before buttons disable");
var fadingMenu = characterMenu.DeepClone(); fadingMenu["options"] = new JsonArray("IRONCLAD", "SILENT");
Check(!GameState.Ready(fadingMenu), "leftover character buttons are a transition even without action context");
Check(!GameState.Ready(fadingMenu, embark), "embark must not settle on leftover characters");
var neow = JsonNode.Parse("""{"state_type":"event","event":{"options":[{"index":0,"title":"Gift A"},{"index":1,"title":"Gift B"},{"index":2,"title":"Gift C"}]}}""")!;
Check(GameState.Ready(neow, embark) && GameState.Actions(neow).Count == 3, "embark settles at Neow's three choices");
// Hold the fading snapshot longer than the stability threshold, then show Neow.
int transitionReads = 0;
var transitionAdapter = new ScheduledGameAdapter(a => a(),
    () => ++transitionReads <= 5 ? fadingMenu : neow,
    _ => throw new Exception("settling must never execute another action"),
    (_, _, _, _, _, _) => new JsonObject());
using (var transitionRuntime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-transition-" + Guid.NewGuid().ToString("N")), "missing.dll", transitionAdapter))
{
    var settled = await transitionRuntime.Stable(CancellationToken.None, GameState.Fingerprint(characterMenu), embark);
    Check(settled.Text("state_type") == "event" && transitionReads >= 8, "poll through repeated fading frames until Neow is stable");
}
// A rest animation can look stable while only potion disposal is available.
var resting = JsonNode.Parse("""{"state_type":"rest_site","player":{"hp":56,"potions":[{"slot":1,"name":"Fairy in a Bottle"}]},"rest_site":{"options":[],"can_proceed":false}}""")!;
var rested = resting.DeepClone(); rested["rest_site"]!["can_proceed"] = true;
Check(!GameState.HasSuitableActions(GameState.Actions(resting)), "discard-only is not a suitable move");
Check(!GameState.HasSuitableActions(new JsonArray()), "empty actions need a retry");
int retryReads = 0;
var retryAdapter = new ScheduledGameAdapter(a => a(), () => { retryReads++; return rested; },
    _ => throw new Exception("legal-action retry must not mutate"), (_, _, _, _, _, _) => new JsonObject());
using (var retryRuntime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-retry-" + Guid.NewGuid().ToString("N")), "missing.dll", retryAdapter))
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var refreshed = await retryRuntime.RetryLegalSnapshot(resting);
    Check(timer.ElapsedMilliseconds >= 550 && retryReads == 1, "retry waits the preview delay and reads once");
    Check(GameState.Actions(refreshed)[0]!["command"].Text("action") == "proceed", "retry discovers proceed");
    Check(GameState.Fingerprint(refreshed) != GameState.Fingerprint(resting), "retry changes snapshot identity");
    await retryRuntime.RetryLegalSnapshot(refreshed);
    Check(retryReads == 1, "suitable actions do not cause a retry");
    rested = resting.DeepClone();
    refreshed = await retryRuntime.RetryLegalSnapshot(resting);
    Check(retryReads == 2 && !GameState.HasSuitableActions(GameState.Actions(refreshed)), "unchanged retry returns without looping or discarding");
}
// A card can have identical visible frames while its delayed orb/relic hook runs.
// Do not ask the model about that partial state, even when end turn looks legal.
var pendingOrbs = JsonNode.Parse("""
    {"state_type":"boss","battle":{"round":5,"turn":"player","is_play_phase":true,
    "can_end_turn":true,"actions_pending":true},
    "player":{"hand":[{"index":0,"id":"DUALCAST","can_play":true}],
    "orbs":[{"name":"Glass"},{"name":"Glass"}]}}
    """)!;
var resolvedOrbs = pendingOrbs.DeepClone();
resolvedOrbs["battle"]!["actions_pending"] = false;
resolvedOrbs["player"]!["orbs"]![1]!["name"] = "Plasma";
Check(!GameState.Ready(pendingOrbs) && GameState.Actions(pendingOrbs).Count == 0,
    "pending combat effects are neither ready nor actionable");
Check(GameState.Ready(resolvedOrbs), "completed combat effects are actionable");
var pendingChoice = pendingOrbs.DeepClone();
pendingChoice["state_type"] = "hand_select";
pendingChoice["hand_select"] = JsonNode.Parse("""{"cards":[{"index":0,"name":"Defend"}]}""");
Check(GameState.Ready(pendingChoice), "a pending action may still request a player selection");
int orbReads = 0;
var orbAdapter = new ScheduledGameAdapter(a => a(), () => ++orbReads <= 6 ? pendingOrbs : resolvedOrbs,
    _ => throw new Exception("settling must not mutate"), (_, _, _, _, _, _) => new JsonObject());
using (var orbRuntime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-orbs-" + Guid.NewGuid().ToString("N")), "missing.dll", orbAdapter))
{
    var settled = await orbRuntime.Stable(CancellationToken.None);
    Check(orbReads >= 9 && settled["player"]!["orbs"]![1]!.Text("name") == "Plasma",
        "settle waits through unchanged pending frames for the completed random orb");
}
var batchState = JsonNode.Parse("""{"state_type":"monster","player":{"hand":[{"index":0,"id":"STRIKE","description":"Deal 6 damage."},{"index":1,"id":"DEFEND","description":"Gain 5 block."}],"draw_pile_count":5},"battle":{"round":1,"turn":"player"}}""")!;
var batchActions = GameState.Actions(batchState);
var selected = ActionBatch.Select(batchState, batchActions, JsonNode.Parse("""{"action_ids":["a0","a1","a2"]}""")!);
var after = batchState.DeepClone(); after["player"]!["hand"]!.AsArray().RemoveAt(0); after["player"]!["hand"]![0]!["index"] = 0;
Check(ActionBatch.CanContinue(batchState, after, selected[0]), "deterministic batch transition");
Check(ActionBatch.Resolve(batchState, selected[1], after)!["command"]!["card_index"]!.GetValue<int>() == 0, "shifted hand index");
after["player"]!["hand"]!.AsArray().Add(JsonNode.Parse("""{"index":1,"id":"SURPRISE"}"""));
Check(!ActionBatch.CanContinue(batchState, after, selected[0]), "unexpected generated card stops batch");
var emptyHand = batchState.DeepClone(); emptyHand["player"]!["hand"]!.AsArray().Clear();
var lastCard = batchState.DeepClone(); lastCard["player"]!["hand"]!.AsArray().RemoveAt(1);
Check(ActionBatch.ForcedEndTurn(lastCard, emptyHand, selected[0])?["command"].Text("action") == "end_turn", "empty hand ends turn without model judgment");
emptyHand["player"]!["potions"] = JsonNode.Parse("""[{"slot":0,"can_use_in_combat":false}]""");
Check(ActionBatch.ForcedEndTurn(lastCard, emptyHand, selected[0]) != null, "potion discards do not delay end turn");
emptyHand["player"]!["potions"]![0]!["can_use_in_combat"] = true;
Check(ActionBatch.ForcedEndTurn(lastCard, emptyHand, selected[0]) == null, "usable potion requires judgment");
Check(ActionBatch.ForcedEndTurn(batchState, after, selected[0]) == null, "unexpected hand change cannot auto end");
var remainingCard = batchState.DeepClone(); remainingCard["player"]!["hand"]!.AsArray().RemoveAt(0);
Check(ActionBatch.ForcedEndTurn(batchState, remainingCard, selected[0]) == null, "remaining playable card requires judgment");
emptyHand["player"]!["potions"]!.AsArray().Clear();
lastCard["player"]!["hand"]![0]!["description"] = "Deal random damage.";
Check(ActionBatch.ForcedEndTurn(lastCard, emptyHand, selected[0]) == null, "random effect requires observation even without hand changes");
lastCard["player"]!["hand"]![0]!["description"] = "Deal 6 damage.";
emptyHand["battle"]!["can_end_turn"] = false;
Check(ActionBatch.ForcedEndTurn(lastCard, emptyHand, selected[0]) == null, "unavailable end turn cannot be appended");
batchState["player"]!["hand"]![0]!["description"] = "Draw 2 cards.";
try { ActionBatch.Select(batchState, GameState.Actions(batchState), JsonNode.Parse("""{"action_ids":["a0","a1"]}""")!); throw new Exception("unsafe batch accepted"); }
catch (InvalidOperationException) { }
var shopState = JsonNode.Parse("""{"state_type":"shop","shop":{"items":[{"index":0,"category":"card","card_name":"Strike","price":50,"is_stocked":true,"can_afford":true},{"index":1,"category":"potion","potion_name":"Fire Potion","price":60,"is_stocked":true,"can_afford":true},{"index":2,"category":"relic","relic_name":"Capsule","price":150,"is_stocked":true,"can_afford":false}]},"player":{"potions":[]}}""")!;
var shopActions = GameState.Actions(shopState);
Check(shopActions.Count == 2, "shop enumerates stocked affordable items");
var shopBatch = ActionBatch.Select(shopState, shopActions, JsonNode.Parse("""{"action_ids":["a0","a1"]}""")!);
var shopAfter = shopState.DeepClone(); shopAfter["shop"]!["items"]![0]!["is_stocked"] = false;
Check(ActionBatch.CanContinue(shopState, shopAfter, shopBatch[0]), "shop batch continues on the same screen");
Check(ActionBatch.Resolve(shopState, shopBatch[1], shopAfter)!["command"]!.Text("index") == "1", "shop batch re-resolves the remaining item");
var rewardsState = JsonNode.Parse("""{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"gold","name":"gold"},{"index":1,"type":"relic","name":"Capsule"}],"can_proceed":true},"player":{"potions":[]}}""")!;
var rewardsActions = GameState.Actions(rewardsState);
Check(ActionBatch.Select(rewardsState, rewardsActions, JsonNode.Parse("""{"action_ids":["a0","a1","a2"]}""")!).Count == 3, "rewards batch with proceed last");
try { ActionBatch.Select(rewardsState, rewardsActions, JsonNode.Parse("""{"action_ids":["a2","a0"]}""")!); throw new Exception("proceed not last accepted"); }
catch (InvalidOperationException) { }
Console.WriteLine("PASS public state, legal actions, and batch boundaries");
foreach (var api in new[] { "responses", "chat_completions" })
foreach (var mode in new[] { "success", "timeout", "stale", "invalid", "recover", "stop", "auto_end", "explicit_end", "vigor_beam" })
{
    var directory = Path.Combine(Path.GetTempPath(), "spire-buddy-test-" + Guid.NewGuid().ToString("N"));
    var handler = new FakeHandler(api, mode);
    using var runtime = new BotRuntime(directory, "missing.dll", handler.Game, handler);
    handler.Runtime = runtime;
    await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api, ["api_key"] = "private-test-key" });
    Check(File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("private-test-key"), "key persists across restarts");
    var savedStatus = await runtime.Dispatch("GET", "/status", null);
    Check(!savedStatus.ToJsonString().Contains("private-test-key"), "status must not echo the key");
    Check(savedStatus["config"]!.Flag("has_api_key"), "key presence reported");
    // The panel shows the stored key as a mask; listing models with no key or
    // with the echoed mask must authenticate with the stored key.
    var listed = await runtime.Dispatch("POST", "/models", new JsonObject { ["api_endpoint"] = "http://model/v1" });
    Check(listed["models"]!.AsArray().Count == 1, "model listing uses the stored key");
    var masked = await runtime.Dispatch("POST", "/models", new JsonObject { ["api_endpoint"] = "http://model/v1", ["api_key"] = BotRuntime.MaskedKey });
    Check(masked["models"]!.AsArray().Count == 1, "echoed mask keeps the stored key");
    runtime.StartGameplay("test", "run");
    if (mode == "stop") { await handler.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await runtime.Dispatch("POST", "/stop", null); }
    JsonNode status;
    var deadline = DateTime.UtcNow.AddSeconds(15);
    do { await Task.Delay(50); status = await runtime.Dispatch("GET", "/status", null); } while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
    Check(!status.Flag("thread_alive"), "worker completion " + api + mode);
    Check(handler.Posts == (mode is "stale" or "invalid" or "stop" ? 0 : mode == "vigor_beam" ? 2 : mode == "explicit_end" ? 4 : mode == "auto_end" ? 3 : 1), "exactly once mutation " + api + mode);
    Check(status.Text("status") == (mode is "success" or "stop" or "recover" or "auto_end" or "explicit_end" or "vigor_beam" ? "idle" : "error"), "status " + api + mode + status.ToJsonString());
    if (mode == "auto_end") Check(handler.ModelCalls == 1, "two cards and end turn use one model call " + api);
    if (mode == "explicit_end") Check(handler.ModelCalls == 1, "Leap, both Defends and explicit end turn use one model call " + api);
    if (mode == "vigor_beam") Check(handler.ModelCalls == 1, "Null then Sweeping Beam use one model call after Vigor expires " + api);
    if (mode == "recover") Check(handler.ModelCalls == 2, "rejected batch fed back once, then corrected");
        if (mode == "success")
        {
            Check(handler.MessageVisibleBeforeAction, "agent message is published before action execution");
            Check(handler.SawRejectedAction, "bundled take_action must be rejected, not executed");
            await runtime.Dispatch("POST", "/message", new JsonObject { ["message"] = "Explain the route." });
            for (int i = 0; i < 100; i++)
            {
                status = await runtime.Dispatch("GET", "/status", null);
                if (status["messages"].Items().Any(e => e.Text("event") == "chat_reply")) break;
                await Task.Delay(20);
            }
            Check(status["messages"].Items().Any(e => e.Text("event") == "chat_reply" && e.Text("message") == "A safe path."), "persistent Buddy chat");
            Check(handler.Posts == 1, "chat must not mutate");
            Check(handler.KnowledgeReads >= 1, "chat consults the run history knowledge");
            Check(handler.SawLiveBrief, "chat grounds answers in the current public state");
        using var reloaded = new BotRuntime(directory, "missing.dll", handler.Game, handler);
        var reloadedStatus = await reloaded.Dispatch("GET", "/status", null);
        Check(reloadedStatus["last_goal"] == null && reloadedStatus["config"]?["goal"] == null, "goal removed from status and settings");
    }
    Console.WriteLine($"PASS {api} {mode}");
}
// The composer stays useful while the bot is idle: a message before any start
// must answer from a freshly read public state, with tools, mutating nothing.
foreach (var api in new[] { "responses", "chat_completions" })
{
    var directory = Path.Combine(Path.GetTempPath(), "spire-buddy-idle-" + Guid.NewGuid().ToString("N"));
    var handler = new FakeHandler(api, "success");
    using var runtime = new BotRuntime(directory, "missing.dll", handler.Game, handler);
    handler.Runtime = runtime;
    await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api, ["api_key"] = "idle-key" });
    await runtime.Dispatch("POST", "/message", new JsonObject { ["message"] = "How many cards do I have?" });
    JsonNode idleStatus = new JsonObject();
    var idleDeadline = DateTime.UtcNow.AddSeconds(15);
    do { await Task.Delay(20); idleStatus = await runtime.Dispatch("GET", "/status", null); }
    while (!idleStatus["messages"].Items().Any(e => e.Text("event") is "chat_reply" or "chat_error") && DateTime.UtcNow < idleDeadline);
    Check(idleStatus["messages"].Items().Any(e => e.Text("event") == "chat_reply" && e.Text("message") == "A safe path."), "chat answers before the first start " + api);
    Check(handler.Posts == 0, "idle chat never mutates " + api);
    Check(handler.SawLiveBrief && handler.KnowledgeReads >= 1, "idle chat grounds in fresh state and history " + api);
    Console.WriteLine($"PASS {api} idle chat");
}
var assembly = Environment.GetEnvironmentVariable("STS2_GAME_ASSEMBLY");
if (assembly != null)
{
    var directory = Path.Combine(Path.GetTempPath(), "spire-buddy-decompile-" + Guid.NewGuid().ToString("N"));
    var decompiler = new EnemyDecompiler(assembly, directory);
    var result = await decompiler.Lookup("Nibbit", CancellationToken.None);
    Check(result["decompile_error"] == null && result["matches"]!.AsArray().Count > 0, "DLL decompilation " + result);
    Check(result["matches"]![0]!.Text("decompiled_source").Contains("GenerateMoveStateMachine"), "move source");
    Check(!result["matches"]![0]!.Text("decompiled_source").Contains(".AddBranch")
        || result["decompilation_notes"]?["RandomBranchState.AddBranch"] != null, "real AddBranch source includes contextual notes");
    var registryId = await decompiler.Lookup("TEST_SUBJECT", CancellationToken.None);
    Check(registryId["decompile_error"] == null && registryId["matches"]!.AsArray().Any(m => m.Text("name") == "TestSubject"), "registry enemy ID resolves independently of localization: " + registryId);
    var instanceLabel = await decompiler.Lookup("Test Subject #C10", CancellationToken.None);
    Check(instanceLabel["decompile_error"] == null && instanceLabel["matches"]!.AsArray().Any(m => m.Text("name") == "TestSubject"), "instance-labelled enemy resolves to its static type: " + instanceLabel);
    var second = new EnemyDecompiler(assembly, directory);
    Check(JsonNode.DeepEquals(result, await second.Lookup("Nibbit", CancellationToken.None)), "disk cache");
    Console.WriteLine("PASS real game DLL decompilation and persistent cache");
}
Console.WriteLine("All native runtime checks passed.");

sealed class AutomaticAdapter : IGameAdapter
{
    JsonNode state = JsonNode.Parse("""{"state_type":"map","map":{"next_options":[{"index":0,"type":"Monster"}]},"player":{"potions":[]}}""")!;
    public int Commands { get; private set; }

    public Task<JsonNode> ReadState(CancellationToken ct) => Task.FromResult(state.DeepClone());

    public Task<JsonNode> Execute(JsonNode command, string expectedSnapshot, Func<bool> mayExecute, CancellationToken ct)
    {
        if (!mayExecute()) return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "error", ["executed"] = false });
        Commands++;
        state = JsonNode.Parse("""{"state_type":"game_over"}""")!;
        return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "ok" });
    }

    public Task<JsonNode> Search(string query, string itemType, string rarity, string? character, int? offset, int? count, CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());

    public Task<JsonNode> ReadKnowledge(CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());
}

sealed class AutomaticShopAdapter : IGameAdapter
{
    JsonNode state = JsonNode.Parse("""{"state_type":"shop","shop":{"can_open":true,"can_proceed":true},"player":{"potions":[]}}""")!;
    public int Commands { get; private set; }

    public Task<JsonNode> ReadState(CancellationToken ct) => Task.FromResult(state.DeepClone());

    public Task<JsonNode> Execute(JsonNode command, string expectedSnapshot, Func<bool> mayExecute, CancellationToken ct)
    {
        if (!mayExecute()) return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "error", ["executed"] = false });
        Commands++;
        state = command.Text("action") switch
        {
            "open_shop" => JsonNode.Parse("""{"state_type":"shop","shop":{"can_close":true,"can_proceed":true,"items":[]},"player":{"potions":[]}}""")!,
            "close_shop" => JsonNode.Parse("""{"state_type":"shop","shop":{"can_open":true,"can_proceed":true},"player":{"potions":[]}}""")!,
            "proceed" => JsonNode.Parse("""{"state_type":"game_over"}""")!,
            _ => throw new Exception("Unexpected automatic shop action: " + command.Text("action"))
        };
        return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "ok" });
    }

    public Task<JsonNode> Search(string query, string itemType, string rarity, string? character, int? offset, int? count, CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());


    public Task<JsonNode> ReadKnowledge(CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());
}

sealed class AutomaticTreasureAdapter : IGameAdapter
{
    JsonNode state = JsonNode.Parse("""{"state_type":"treasure","treasure":{"can_open":true},"player":{"potions":[]}}""")!;
    public int Commands { get; private set; }

    public Task<JsonNode> ReadState(CancellationToken ct) => Task.FromResult(state.DeepClone());

    public Task<JsonNode> Execute(JsonNode command, string expectedSnapshot, Func<bool> mayExecute, CancellationToken ct)
    {
        if (!mayExecute()) return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "error", ["executed"] = false });
        Commands++;
        state = command.Text("action") switch
        {
            "open_chest" => JsonNode.Parse("""{"state_type":"treasure","treasure":{"relics":[{"index":0,"name":"Capsule"}]},"player":{"potions":[]}}""")!,
            "claim_treasure_relic" => JsonNode.Parse("""{"state_type":"treasure","treasure":{"can_proceed":true},"player":{"potions":[]}}""")!,
            "proceed" => JsonNode.Parse("""{"state_type":"game_over"}""")!,
            _ => throw new Exception("Unexpected automatic treasure action: " + command.Text("action"))
        };
        return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "ok" });
    }

    public Task<JsonNode> Search(string query, string itemType, string rarity, string? character, int? offset, int? count, CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());

    public Task<JsonNode> ReadKnowledge(CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());
}

sealed class AutomaticTransformAdapter : IGameAdapter
{
    // Mirrors the deck transform screen after a card was picked: the preview is
    // showing with confirm and cancel available.
    JsonNode state = JsonNode.Parse("""{"state_type":"card_select","card_select":{"screen_type":"transform","preview_showing":true,"preview_cards":[{"index":0,"name":"Defend"}],"cards":[{"index":0,"name":"Defend","selected":true}],"selected_indices":[0],"can_confirm":true,"can_cancel":true},"player":{"potions":[]}}""")!;
    public int Commands { get; private set; }
    public string Executed { get; private set; } = "";

    public Task<JsonNode> ReadState(CancellationToken ct) => Task.FromResult(state.DeepClone());

    public Task<JsonNode> Execute(JsonNode command, string expectedSnapshot, Func<bool> mayExecute, CancellationToken ct)
    {
        if (!mayExecute()) return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "error", ["executed"] = false });
        if (command.Text("action") != "confirm_selection") throw new Exception("Unexpected transform action: " + command.Text("action"));
        Commands++; Executed = command.Text("action");
        state = JsonNode.Parse("""{"state_type":"game_over"}""")!;
        return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "ok" });
    }

    public Task<JsonNode> Search(string query, string itemType, string rarity, string? character, int? offset, int? count, CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());


    public Task<JsonNode> ReadKnowledge(CancellationToken ct) => Task.FromResult<JsonNode>(new JsonObject());
}

sealed class FakeHandler(string api, string mode) : HttpMessageHandler
{
    public int Posts;
    public bool SawRejectedAction;
    public bool MessageVisibleBeforeAction;
    public bool SawLiveBrief;
    public int KnowledgeReads;
    public int ModelCalls;
    public BotRuntime? Runtime;
    public TaskCompletionSource ModelEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IGameAdapter Game => new ScheduledGameAdapter(action => action(),
        () => mode == "vigor_beam" && Posts < 2 ? VigorBeamState() : mode == "explicit_end" && Posts < 4 ? ExplicitEndState() : mode == "auto_end" && Posts < 3 ? CombatState() : Posts > 0 ? JsonNode.Parse("""{"state_type":"game_over"}""")! :
            new JsonObject { ["state_type"] = "map", ["map"] = new JsonObject { ["next_options"] = new JsonArray(
                new JsonObject { ["index"] = mode == "stale" && ModelCalls > 0 ? 1 : 0 },
                new JsonObject { ["index"] = 2 }) } },
        command =>
        {
            var status = Runtime!.Dispatch("GET", "/status", null).GetAwaiter().GetResult();
            MessageVisibleBeforeAction = status["messages"].Items().Any(e =>
                e.Text("event") == "agent_message" && e.Text("message") == "Taking the path.");
            if (mode == "auto_end" && command.Text("action") != (Posts < 2 ? "play_card" : "end_turn"))
                throw new Exception("Expected two card plays followed by automatic end turn.");
            if (mode == "explicit_end")
            {
                var expectedIndex = Posts == 0 ? 0 : 2;
                if (command.Text("action") != (Posts < 3 ? "play_card" : "end_turn") ||
                    Posts < 3 && command["card_index"]!.GetValue<int>() != expectedIndex)
                    throw new Exception("Expected Leap, shifted Defends, then explicit end turn.");
            }
            if (mode == "vigor_beam" &&
                (command.Text("action") != "play_card" ||
                 command["card_index"]!.GetValue<int>() != (Posts == 0 ? 1 : 2)))
                throw new Exception("Expected Null then shifted Sweeping Beam.");
            Posts++;
            if (mode == "timeout") throw new TaskCanceledException("uncertain mutation");
            return new JsonObject { ["status"] = "ok" };
        },
        (query, itemType, rarity, character, offset, count) => new JsonObject { ["results"] = new JsonArray() },
        () => { KnowledgeReads++; return JsonNode.Parse("""{"odds":{"status":"ok","potion_reward_chance_normal_combat":"40%"},"run_history":{"status":"ok","win_rate":"50%","runs":[]}}""")!; });
    JsonNode VigorBeamState()
    {
        var state = JsonNode.Parse("""
            {"state_type":"monster","player":{"energy":2,"draw_pile_count":23,"hand":[
            {"index":0,"id":"REFRACT","cost":3,"description":"Deal 18 damage twice. Channel 2 Glass.","can_play":false,"unplayable_reason":"EnergyCostTooHigh"},
            {"index":1,"id":"NULL","cost":0,"description":"Deal 21 damage. Apply 2 Weak. Channel 1 Dark.","can_play":true},
            {"index":2,"id":"DUALCAST","cost":1,"description":"Evoke your rightmost Orb twice.","can_play":true},
            {"index":3,"id":"SWEEPING_BEAM","cost":1,"description":"Deal 17 damage to ALL enemies. Draw 1 card.","can_play":true}]},
            "battle":{"round":1,"turn":"player"}}
            """)!;
        if (Posts == 1)
        {
            var hand = state["player"]!["hand"]!.AsArray();
            hand.RemoveAt(1);
            for (int i = 0; i < hand.Count; i++) hand[i]!["index"] = i;
            hand[0]!["description"] = "Deal 10 damage twice. Channel 2 Glass.";
            hand[2]!["description"] = "Deal 9 damage to ALL enemies. Draw 1 card.";
        }
        return state;
    }
    JsonNode ExplicitEndState()
    {
        var state = JsonNode.Parse("""
            {"state_type":"monster","player":{"energy":2,"draw_pile_count":20,
            "potions":[{"slot":1,"name":"Fairy in a Bottle","can_use_in_combat":false}],
            "hand":[
            {"index":0,"id":"LEAP","cost":1,"description":"Gain 9 Block.","can_play":true,"unplayable_reason":null},
            {"index":1,"id":"DAZED","cost":0,"can_play":false,"unplayable_reason":"HasUnplayableKeyword"},
            {"index":2,"id":"DUALCAST","cost":1,"can_play":true,"unplayable_reason":null},
            {"index":3,"id":"DEFEND","cost":0,"description":"Gain 5 Block.","can_play":true,"unplayable_reason":null},
            {"index":4,"id":"DEFEND","cost":1,"description":"Gain 5 Block.","can_play":true,"unplayable_reason":null}]},
            "battle":{"round":2,"turn":"player"}}
            """)!;
        var hand = state["player"]!["hand"]!.AsArray();
        if (Posts > 0) hand.RemoveAt(0);
        if (Posts > 1) hand.RemoveAt(2);
        if (Posts > 2) hand.RemoveAt(2);
        for (int i = 0; i < hand.Count; i++) hand[i]!["index"] = i;
        state["player"]!["energy"] = Posts == 0 ? 2 : Posts < 3 ? 1 : 0;
        if (Posts == 3)
        {
            hand[1]!["can_play"] = false;
            hand[1]!["unplayable_reason"] = "EnergyCostTooHigh";
        }
        return state;
    }
    JsonNode CombatState()
    {
        var state = JsonNode.Parse("""{"state_type":"monster","player":{"hand":[{"index":0,"id":"STRIKE","description":"Deal 6 damage."},{"index":1,"id":"DEFEND","description":"Gain 5 block."}],"draw_pile_count":5,"potions":[{"slot":0,"can_use_in_combat":false}]},"battle":{"round":1,"turn":"player"}}""")!;
        for (int i = 0; i < Posts; i++) state["player"]!["hand"]!.AsArray().RemoveAt(0);
        if (Posts == 1) state["player"]!["hand"]![0]!["index"] = 0;
        return state;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        JsonNode response;
        if (request.RequestUri!.Host != "model") throw new Exception("Unexpected HTTP request: " + request.RequestUri);
        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/models"))
        {
            if (request.Headers.Authorization?.Parameter != "private-test-key")
                throw new Exception("Model listing must authenticate with the stored key, not the masked or missing value.");
            var models = new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "gpt-test" }) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(models.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        {
            var raw = await request.Content!.ReadAsStringAsync(ct);
            if (raw.Contains("\\u0022")) throw new Exception("request body must not use \\u0022 escaping");
            if (raw.Contains("must be submitted alone")) SawRejectedAction = true;
            var body = JsonNode.Parse(raw)!;
            var history = body[api == "responses" ? "input" : "messages"]!.AsArray();
            if (api != "responses" && history.Any(n => n.Text("role") == "developer"))
                throw new Exception("developer role must not reach chat completions providers");
            var lastText = history.Last(n => n.Text("role") == "user")!.Text("content");
            if (lastText is "Explain the route." or "How many cards do I have?")
            {
                if (body["tools"] is not JsonArray chatTools || chatTools.Count == 0) throw new Exception("Chat must offer read-only tools");
                if (chatTools.OfType<JsonObject>().Any(t => t.Text("name") == "take_action")) throw new Exception("Chat must not offer mutations");
                if (history.Any(n => n.Text("role") == "user" && n.Text("content").Contains("Current public game state"))) SawLiveBrief = true;
                var answered = history.Any(n => n.Text("type") == "function_call_output" || n.Text("role") == "tool");
                if (!answered) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ToolResponse(("review_run_history", "{}")).ToJsonString()) };
                var toolSawKnowledge = history.Any(n => (n.Text("type") == "function_call_output" && n.Text("output").Contains("win_rate"))
                    || (n.Text("role") == "tool" && n.Text("content").Contains("win_rate")));
                if (!toolSawKnowledge) throw new Exception("Chat tool results must reach the model");
                var reply = api == "responses"
                    ? JsonNode.Parse("""{"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"A safe path."}]}]}""")!
                    : JsonNode.Parse("""{"choices":[{"message":{"role":"assistant","content":"A safe path."}}]}""")!;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString()) };
            }
            var brief = Regex.Match(lastText, @"snapshot=([0-9A-Fa-f]{16})").Groups[1].Value;
            if (brief.Length == 0) throw new Exception("user message must open with a 16-hex snapshot id");
            if (!lastText.Contains("[Actions]")) throw new Exception("user message must list legal actions");
            if (lastText.Contains("\"keywords\"") || lastText.Contains("\"target_type\"")) throw new Exception("user message must not embed card JSON");
            if (mode == "stale" && ModelCalls > 0) throw new InvalidOperationException("Stop after proving stale action was withheld.");
            ModelCalls++;
            ModelEntered.TrySetResult();
            if (mode == "stop") await Task.Delay(200, ct);
            var ids = mode == "vigor_beam" ? new JsonArray("a0", "a2") : mode == "explicit_end" ? new JsonArray("a0", "a2", "a3", "a5") : mode == "auto_end" ? new JsonArray("a0", "a1") : mode == "invalid" ? new JsonArray("invented") : mode == "recover" && ModelCalls == 1 ? new JsonArray("a0", "a0") : new JsonArray("a0");
            var args = new JsonObject { ["snapshot_id"] = brief.ToLowerInvariant(), ["action_ids"] = ids, ["message"] = "Taking the path.", ["rationale"] = "test", ["plan"] = "win" }.ToJsonString();
            response = mode == "success" && ModelCalls == 1
                ? ToolResponse(("list_legal_actions", "{}"), ("review_recent_actions", "{}"))
                : mode == "success" && ModelCalls == 2
                    ? ToolResponse(("take_action", args), ("list_legal_actions", "{}"))
                    : ToolResponse(("take_action", args));
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    JsonNode ToolResponse(params (string Name, string Args)[] calls) =>
        api == "responses"
            ? new JsonObject { ["output"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject { ["type"] = "function_call", ["call_id"] = c.Name, ["name"] = c.Name, ["arguments"] = c.Args }).ToArray()) }
            : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject { ["id"] = c.Name, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args } }).ToArray()) } }) };
}
