using System.Net;
using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class JevStrategyChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static JsonNode Over() => new JsonObject { ["state_type"] = "game_over" };
    static JsonNode Player(int slots = 2)
    {
        var player = JsonNode.Parse("""{"hp":50,"max_hp":70,"gold":60,"deck":[],"relics":[],"max_potion_slots":2,"potions":[{"slot":0,"id":"OLD_A","name":"Old A"},{"slot":1,"id":"OLD_B","name":"Old B"}]}""")!;
        player["max_potion_slots"] = slots;
        return player;
    }

    // Keep fixture mutations explicit: slots are actual held indices, while
    // reward row indices are rebuilt by the UI after every claim.
    static JsonNode Rewards(int slots = 2) => new JsonObject
    {
        ["state_type"] = "rewards", ["player"] = Player(slots),
        ["rewards"] = JsonNode.Parse("""{"can_proceed":true,"items":[{"index":0,"reward_id":10,"type":"potion","potion_id":"NEW","potion_name":"New Potion","potion_description":"Gain strength."},{"index":1,"reward_id":11,"type":"relic","description":"New relic"},{"index":2,"reward_id":12,"type":"gold","gold_amount":25},{"index":3,"reward_id":13,"type":"card"}]}""")
    };
    static JsonNode Shop(string kind = "shop")
    {
        var data = JsonNode.Parse("""{"can_close":true,"inventory_open":true,"items":[{"index":0,"category":"potion","potion_id":"NEW","potion_name":"New Potion","potion_description":"Gain strength.","price":50,"is_stocked":true,"can_afford":true},{"index":1,"category":"card","card_name":"Cheap Card","price":30,"is_stocked":true,"can_afford":true},{"index":2,"category":"relic","relic_name":"Too Expensive","price":61,"is_stocked":true,"can_afford":true,"keywords":[{"name":"OmittedRule","description":"Unused."}]},{"index":3,"category":"potion","potion_name":"Sold Potion","price":5,"is_stocked":false,"can_afford":true}]}""")!;
        return new JsonObject { ["state_type"] = kind, ["player"] = Player(), [kind] = kind == "shop" ? data : new JsonObject { ["shop"] = data } };
    }
    static JsonNode RestSite(int hp = 70)
    {
        var player = Player(); player["hp"] = hp;
        return new JsonObject
        {
            ["state_type"] = "rest_site", ["player"] = player,
            ["rest_site"] = JsonNode.Parse("""{"options":[{"index":0,"id":"HEAL","name":"Rest","description":"Heal for 30% of your Max HP (21).","is_enabled":true},{"index":1,"id":"SMITH","name":"Smith","description":"Upgrade a card in your Deck.","is_enabled":true}],"can_proceed":false}""")
        };
    }

    internal static async Task Run()
    {
        Options();
        RestOptions();
        ReplacementValidation();
        await RewardsRun(freeSlot: true);
        await RewardsRun(freeSlot: false);
        await MultiplePotions();
        foreach (var (first, second) in new[] { (true, true), (true, false), (false, true), (false, false) })
            await MultipleCards(first, second);
        await MultipleCards(true, true, resumeInSelection: true);
        CardRewardTracking();
        await ShopRun();
        await ShopBudgetQuestions();
        await RestQuestions();
        await InterruptedReplacement(stop: true);
        await InterruptedReplacement(stop: false);
        await DisabledStrategy();
        Console.WriteLine("PASS Jev reward order, separate card rewards/skip/resume, potion replacement/skip, shop budgets/removal/exit, rest-site questions, and interrupted replacements");
    }

    static void Options()
    {
        var policy = new JevStrategy();
        var state = Rewards();
        foreach (var expected in new[] { "gold", "card", "relic" })
        {
            var options = policy.Prepare(state, GameState.Actions(state));
            Check(options.Automatic != null && options.Actions.Count == 1, "routine reward is automatic");
            var index = options.Automatic!["command"].Num("index")!.Value;
            Check(state["rewards"]!["items"]![index].Text("type") == expected, "gold then cards then relics, regardless of row order");
            RemoveReward(state, index);
        }
        var potions = policy.Prepare(state, GameState.Actions(state));
        Check(potions.Automatic == null && potions.Actions.Count == 3, "full inventory offers one replacement per held slot plus skip");
        Check(potions.Actions.Items().Count(a => a["command"].Text("action") == "replace_potion") == 2, "no separate unusable claim/discard/proceed choices");
        Check(potions.Actions[1].Text("summary").Contains("slot 1 (Old B)"), "replacement identifies the current held index and name");
        policy.Skip(state, potions.Actions.Items().Single(a => a["command"].Text("action") == "skip_potion_reward"));
        var duplicate = state["rewards"]!["items"]![0]!.DeepClone(); duplicate["reward_id"] = 14; duplicate["index"] = 1;
        state["rewards"]!["items"]!.AsArray().Add(duplicate);
        var next = policy.Prepare(state, GameState.Actions(state));
        Check(next.Actions[0]?["take"]?["command"].Num("index") == 1, "identical potion rewards are distinct choices");
        RemoveReward(state, 0); // The skipped row disappears and the second row shifts.
        next = policy.Prepare(state, GameState.Actions(state));
        Check(next.Actions[0]?["take"]?["command"].Num("index") == 0, "a different potion is not skipped when indices shift");
        policy.Skip(state, next.Actions.Items().Single(a => a["command"].Text("action") == "skip_potion_reward"));
        var done = policy.Prepare(state, GameState.Actions(state));
        Check(GameState.ForcedAction(state, done.Actions)?["command"].Text("action") == "proceed", "skipped potions do not block automatic proceed");
        var brief = GameState.JevBrief(state, "Win.", "", new JsonArray(), "", policy);
        Check(!brief.Contains("New Potion") && state["rewards"]!["items"]!.AsArray().Count == 1, "skipped rewards are omitted without mutating the game snapshot");
        state["rewards"]!["items"]![0]!["index"] = 1;
        state["rewards"]!["items"]!.AsArray().Insert(0, new JsonObject { ["index"] = 0, ["reward_id"] = 15, ["type"] = "card_removal" });
        Check(policy.Prepare(state, GameState.Actions(state)).Actions.Items().All(a => a["command"].Text("action") is not ("replace_potion" or "skip_potion_reward")), "a skipped reward stays skipped when its row moves");
        RemoveReward(state, 0);
        Check(GameState.ForcedAction(state, policy.Prepare(state, GameState.Actions(state)).Actions)?["command"].Text("action") == "proceed", "removing an earlier reward does not revive a skipped potion");

        foreach (var kind in new[] { "shop", "fake_merchant" })
        {
            state = Shop(kind);
            var options = policy.Prepare(state, GameState.Actions(state));
            var choices = options.Actions;
            Check(options.Question!.Contains("You have 60 gold") && choices[0].Text("summary").Contains("10g left"), "normal and event shops provide the actual budget and replacement cost");
            Check(choices.Count == 4 && choices.Items().Count(a => a["command"].Text("action") == "replace_potion") == 2, "shop keeps affordable card, replacements and close");
            Check(GameState.ForcedAction(state, choices) == null, "affordable inventory must not auto-close");
            brief = GameState.JevBrief(state, "Win.", "", new JsonArray(), "");
            Check(brief.Contains("New Potion") && brief.Contains("Cheap Card") && !brief.Contains("Too Expensive") && !brief.Contains("Sold Potion") && !brief.Contains("OmittedRule"), "shop state and glossary include only stocked affordable items");
            JevStrategy.Shop(state)!["items"]![1]!["is_stocked"] = false;
            choices = policy.Prepare(state, GameState.Actions(state)).Actions;
            Check(choices.Count == 3 && GameState.ForcedAction(state, choices) == null, "replacement-only inventory must not auto-close");
            state["player"]!["gold"] = 50;
            Check(policy.Prepare(state, GameState.Actions(state)).Actions.Items().Any(a => a["command"].Text("action") == "replace_potion"), "exact gold balance can buy a potion replacement");
            state["player"]!["gold"] = 20;
            choices = policy.Prepare(state, GameState.Actions(state)).Actions;
            Check(choices.Count == 1 && GameState.ForcedAction(state, choices)?["command"].Text("action") == "close_shop", "prices are rechecked against the latest gold even with stale can_afford flags");
            JevStrategy.Shop(state)!["items"]![0]!["price"] = 0;
            Check(policy.Prepare(state, GameState.Actions(state)).Actions.Count == 3, "free stocked items remain available");
        }
    }

    static void RestOptions()
    {
        var policy = new JevStrategy();
        foreach (int hp in new[] { 70, 69, 12 })
        {
            var state = RestSite(hp); var legal = GameState.Actions(state);
            var snapshot = state.WriteString(); var originalActions = legal.WriteString();
            var options = policy.Prepare(state, legal);
            Check(options.Actions.Count == 2 && options.Automatic == null && GameState.ForcedAction(state, options.Actions) == null,
                "rest and smith remain genuine decisions without potion-discard noise");
            Check(options.Question!.Contains($"{hp}/70 HP; {70 - hp} HP is missing")
                && options.Actions[0].Text("summary").Contains(hp == 70 ? "restore 0 HP" : $"capped at {70 - hp} missing HP"),
                "full and partial healing use current missing HP, not the nominal 21-HP label");
            Check(options.Actions[1].Text("summary").Contains("permanently upgrade") && options.Question.Contains("visible non-healing effect"),
                "upgrades are explicit without overriding visible rest benefits");
            Check(options.Actions.Items().All(a => JsonNode.DeepEquals(a["command"], legal.Items().Single(l => l.Text("id") == a.Text("id"))["command"])),
                "rest-site annotations retain executable IDs and commands");
            Check(snapshot == state.WriteString() && originalActions == legal.WriteString(), "rest-site preparation must not change the snapshot or legal actions");
        }
        var localized = RestSite();
        var rows = localized["rest_site"]!["options"]!.AsArray();
        rows[0]!["name"] = "休息"; rows[0]!["index"] = 7;
        rows[1]!["name"] = "锻造"; rows[1]!["index"] = 3;
        var first = rows[0]!; rows.RemoveAt(0); rows.Add(first);
        var choices = policy.Prepare(localized, GameState.Actions(localized)).Actions;
        Check(choices[0].Text("summary").Contains("permanently upgrade") && choices[1].Text("summary").Contains("restore 0 HP"),
            "rest-site annotations use stable option IDs regardless of language and position");
        rows[0]!["is_enabled"] = false;
        rows.Add(JsonNode.Parse("""{"index":8,"id":"CUSTOM","name":"Custom option","description":"Another benefit.","is_enabled":true}"""));
        choices = policy.Prepare(localized, GameState.Actions(localized)).Actions;
        Check(choices.Items().Select(c => c["command"].Num("index")).SequenceEqual(new int?[] { 7, 8 }),
            "disabled Smith is not invented; Rest and unknown enabled options remain available");
        foreach (var field in new[] { "hp", "max_hp" })
        {
            var unknown = RestSite(); unknown["player"]![field] = null;
            var options = policy.Prepare(unknown, GameState.Actions(unknown));
            Check(!options.Question!.Contains("You have") && !options.Actions[0].Text("summary").Contains("restore 0 HP"),
                "missing health data does not invent a full-health state");
        }
        var completed = RestSite(); completed["rest_site"]!["options"] = new JsonArray(); completed["rest_site"]!["can_proceed"] = true;
        Check(GameState.ForcedAction(completed, policy.Prepare(completed, GameState.Actions(completed)).Actions)?["command"].Text("action") == "proceed",
            "completed rest sites still proceed automatically with held potions");
    }

    static async Task RestQuestions()
    {
        foreach (var (hp, smith, restBenefit) in new[] { (70, true, false), (12, false, false), (70, false, true) })
        {
            var initial = RestSite(hp);
            var guidance = restBenefit ? "Rest for the visible relic reward, even at full HP." : "Win this run.";
            if (restBenefit) initial["player"]!["relics"]!.AsArray().Add(JsonNode.Parse("""{"id":"TEST_REST_REWARD","name":"Rest Reward","description":"Resting grants a card reward."}"""));
            using var handler = new Scenario(initial);
            handler.Choose = body =>
            {
                var question = body["questions"]!["action0"]!;
                var instructions = question.Text("instructions");
                Check(body.Text("state").Contains(guidance), "player rest-site guidance survives into every Jev request");
                if (handler.State.Text("state_type") == "card_select")
                {
                    Check(!instructions.Contains("Which rest-site option") && body.Text("state").Contains("[Upgrade 0]"),
                        "Smith's next request uses normal card-selection instructions and upgrade previews");
                    return Pick(body, "Neutralize");
                }
                Check(instructions.StartsWith($"You have {hp}/70 HP; {70 - hp} HP is missing")
                    && !instructions.Contains("Gold, opening card rewards"), "the actual wire request uses the focused rest-site question");
                Check(question["criteria"]!.AsObject().Count == 2, "rest-site request offers only the two real rest-site choices");
                if (restBenefit) Check(body.Text("state").Contains("Resting grants a card reward."), "non-healing rest benefits stay visible");
                return Pick(body, smith ? "permanently upgrade" : "rest Rest");
            };
            handler.Apply = command =>
            {
                switch (command.Text("action"))
                {
                    case "choose_rest_option":
                        Check(command.Num("index") == (smith ? 1 : 0), "execute the selected rest option without replacing Jev's decision");
                        if (smith) return new JsonObject
                        {
                            ["state_type"] = "card_select", ["player"] = initial["player"]!.DeepClone(),
                            ["card_select"] = JsonNode.Parse("""{"screen_type":"upgrade","cards":[{"index":0,"name":"Neutralize","description":"Deal 3 damage. Apply 1 Weak.","upgrade_description":"Deal 4 damage. Apply 2 Weak."},{"index":1,"name":"Strike","description":"Deal 6 damage.","upgrade_description":"Deal 9 damage."}]}""")
                        };
                        break;
                    case "select_card": Check(command.Num("index") == 0, "Smith selects the chosen card"); break;
                    case "proceed": return Over();
                    default: throw new Exception("Unexpected rest-site command: " + command);
                }
                var completed = initial.DeepClone();
                completed["rest_site"]!["options"] = new JsonArray(); completed["rest_site"]!["can_proceed"] = true;
                if (!smith) completed["player"]!["hp"] = Math.Min(hp + 21, 70);
                return completed;
            };
            using var runtime = await Runtime(handler);
            runtime.StartGameplay(guidance, "run");
            await Wait(runtime);
            Check(handler.Requests.Count == (smith ? 2 : 1) && handler.Commands.Select(c => c.Text("action")).SequenceEqual(smith
                ? new[] { "choose_rest_option", "select_card", "proceed" } : new[] { "choose_rest_option", "proceed" }),
                "full-health smithing, injured resting and explicit rest benefits keep their choices and automatic exit");
        }
    }

    static void ReplacementValidation()
    {
        var policy = new JevStrategy();
        foreach (bool shop in new[] { false, true })
        {
            var before = shop ? Shop() : Rewards();
            if (!shop) before["rewards"]!["items"] = new JsonArray(before["rewards"]!["items"]![0]!.DeepClone());
            var choice = policy.Prepare(before, GameState.Actions(before)).Actions.Items().First(a => a["command"].Text("action") == "replace_potion");
            var replacement = new JevStrategy.PotionReplacement(before, choice);
            Check(replacement.Take(before) == null, "never take into a full inventory");
            var after = before.DeepClone(); after["player"]!["potions"]!.AsArray().RemoveAt(0);
            Check(replacement.Take(after)?["command"].Text("action") == (shop ? "shop_purchase" : "claim_reward"), "take is resolved only after the intended discard");
            var changed = after.DeepClone(); changed["player"]!["gold"] = 59;
            Check(replacement.Take(changed) == null, "unrelated player changes cancel the second step");
            changed = after.DeepClone();
            if (shop) changed["shop"]!["items"]![0]!["price"] = 51;
            else changed["rewards"]!["items"]![0]!["reward_id"] = 999;
            Check(replacement.Take(changed) == null, "changed price or reward identity cannot be substituted");
            changed = after.DeepClone(); changed["state_type"] = "map";
            Check(replacement.Take(changed) == null, "a screen transition ends the replacement");
        }
    }

    static async Task RewardsRun(bool freeSlot)
    {
        using var handler = new Scenario(Rewards(freeSlot ? 3 : 2));
        JsonNode? back = null;
        var order = new List<string>();
        handler.Choose = body => body.Text("state").StartsWith("screen=card_reward")
            ? Pick(body, freeSlot ? "skip_card_reward" : "First") : Pick(body, "Replace potion slot 1");
        handler.Apply = command =>
        {
            var state = handler.State;
            switch (command.Text("action"))
            {
                case "claim_reward":
                    var index = command.Num("index")!.Value;
                    var item = state["rewards"]!["items"]![index]!;
                    var type = item.Text("type"); order.Add(type);
                    if (type == "potion") AcquirePotion(state, item);
                    if (type == "gold") state["player"]!["gold"] = state["player"]!.Num("gold")!.Value + 25;
                    RemoveReward(state, index);
                    if (type == "card")
                    {
                        back = state.DeepClone();
                        return new JsonObject { ["state_type"] = "card_reward", ["player"] = state["player"]!.DeepClone(), ["card_reward"] = JsonNode.Parse("""{"can_skip":true,"cards":[{"index":0,"name":"First","description":"Gain block."},{"index":1,"name":"Second","description":"Deal damage."}]}""") };
                    }
                    return state;
                case "select_card_reward": case "skip_card_reward": order.Add(command.Text("action")); return back!;
                case "discard_potion": order.Add("discard:" + command["slot"]); Discard(state, command); return state;
                case "proceed": order.Add("proceed"); return Over();
                default: throw new Exception("Unexpected reward command: " + command);
            }
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Win this run.", "run");
        await Wait(runtime);
        var expected = freeSlot ? new[] { "gold", "card", "skip_card_reward", "relic", "potion", "proceed" }
            : new[] { "gold", "card", "select_card_reward", "relic", "discard:1", "potion", "proceed" };
        Check(order.SequenceEqual(expected), "reward workflow order: " + string.Join(",", order));
        Check(handler.Requests.Count == (freeSlot ? 1 : 2), "only card and full-slot potion choices spend Jev calls");
        Check(handler.Commands.All(c => c.Text("action") is not ("replace_potion" or "skip_potion_reward")), "synthetic choices never reach the game adapter");
        Console.WriteLine("PASS Jev reward workflow (free slot=" + freeSlot + ")");
    }

    static async Task MultiplePotions()
    {
        var state = Rewards();
        var first = state["rewards"]!["items"]![0]!.DeepClone();
        var second = first.DeepClone(); second["reward_id"] = 20; second["index"] = 1;
        state["rewards"]!["items"] = new JsonArray(first, second);
        using var handler = new Scenario(state);
        handler.Choose = body => Pick(body, handler.Requests.Count == 1 ? "Skip potion reward" : "Replace potion slot 0");
        handler.Apply = command =>
        {
            if (command.Text("action") == "discard_potion") Discard(handler.State, command);
            else if (command.Text("action") == "claim_reward")
            {
                Check(command.Num("index") == 1, "claim the second potion while leaving the skipped first one");
                AcquirePotion(handler.State, handler.State["rewards"]!["items"]![1]!); RemoveReward(handler.State, 1);
            }
            else if (command.Text("action") == "proceed") return Over();
            else throw new Exception("Unexpected potion command");
            return handler.State;
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Win.", "run");
        await Wait(runtime);
        Check(handler.Requests.Count == 2 && handler.Commands.Count == 3, "skip one potion, replace another, then automatically proceed");
    }

    static async Task MultipleCards(bool skipFirst, bool skipSecond, bool resumeInSelection = false)
    {
        // A declined card offer stays enabled in the real loot UI. Two offers
        // may have identical labels (and even identical cards), but distinct IDs.
        var loot = Rewards();
        loot["run"] = JsonNode.Parse("""{"act":1,"floor":6,"ascension":10}""");
        loot["player"]!["potions"] = new JsonArray();
        loot["rewards"]!["items"] = JsonNode.Parse("""[{"index":0,"reward_id":43,"type":"gold","gold_amount":7},{"index":1,"reward_id":44,"type":"potion","potion_id":"FORTIFIER","potion_name":"Fortifier"},{"index":2,"reward_id":41,"type":"card"},{"index":3,"reward_id":42,"type":"card"}]""");
        JsonNode Offer(int id) => new JsonObject
        {
            ["state_type"] = "card_reward", ["run"] = loot["run"]!.DeepClone(), ["player"] = loot["player"]!.DeepClone(),
            ["card_reward"] = new JsonObject { ["reward_id"] = id, ["can_skip"] = true,
                ["cards"] = JsonNode.Parse("""[{"index":0,"name":"Defend","description":"Gain 5 Block.","cost":1}]""") }
        };
        using var handler = new Scenario(resumeInSelection ? Offer(41) : loot.DeepClone());
        var opened = new List<int>(); var decided = new List<int>();
        handler.Choose = body =>
        {
            var id = handler.State["card_reward"].Num("reward_id")!.Value;
            Check(!decided.Contains(id), "a skipped or accepted card reward must never be reopened");
            decided.Add(id);
            Check(body["questions"]!["action0"].Text("instructions").Contains("compared with adding no card"), "card offers use the focused question");
            return Pick(body, (id == 41 ? skipFirst : skipSecond) ? "Decline this card reward" : "Defend");
        };
        handler.Apply = command =>
        {
            Check(handler.Commands.Count <= 8, "loot sequence must terminate instead of looping");
            switch (command.Text("action"))
            {
                case "claim_reward":
                    var index = command.Num("index")!.Value;
                    var reward = loot["rewards"]!["items"]![index]!;
                    if (reward.Text("type") == "card")
                    {
                        var id = reward.Num("reward_id")!.Value;
                        Check(!opened.Contains(id), "open each individual card offer at most once");
                        opened.Add(id); return Offer(id);
                    }
                    if (reward.Text("type") == "gold") loot["player"]!["gold"] = 67;
                    else AcquirePotion(loot, reward);
                    RemoveReward(loot, index); return loot;
                case "skip_card_reward": return loot; // Deliberately leave the row clickable.
                case "select_card_reward":
                    var chosenId = handler.State["card_reward"].Num("reward_id");
                    var chosen = loot["rewards"]!["items"]!.Items().Single(r => r.Num("reward_id") == chosenId);
                    RemoveReward(loot, chosen.Num("index")!.Value); return loot;
                case "proceed":
                    Check(decided.SequenceEqual(new[] { 41, 42 }), "both card rewards must be resolved before proceeding");
                    Check(loot["player"].Num("gold") == 67 && loot["player"]!["potions"]!.Items().Single().Text("id") == "FORTIFIER", "collect the other loot automatically");
                    return Over();
                default: throw new Exception("Unexpected multi-card command: " + command);
            }
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Win.", "run");
        await Wait(runtime);
        Check(handler.Requests.Count == 2 && opened.Count == (resumeInSelection ? 1 : 2), "one Jev choice per card offer; reward navigation remains automatic");
        Check(loot["rewards"]!["items"]!.AsArray().Count == (skipFirst ? 1 : 0) + (skipSecond ? 1 : 0), "skipped rows stay visible without blocking completion");
    }

    static void CardRewardTracking()
    {
        var policy = new JevStrategy();
        var loot = Rewards(); loot["run"] = new JsonObject { ["floor"] = 6 };
        loot["rewards"]!["items"] = new JsonArray(new JsonObject { ["index"] = 0, ["type"] = "card", ["reward_id"] = 41 });
        var open = policy.Prepare(loot, GameState.Actions(loot)).Automatic!;
        var offer = new JsonObject { ["state_type"] = "card_reward", ["run"] = loot["run"]!.DeepClone(),
            ["card_reward"] = new JsonObject { ["can_skip"] = true } };
        var skip = GameState.Actions(offer).Items().Single();
        policy.Observe(loot, open, offer);
        policy.Observe(offer, skip, offer);
        Check(policy.Prepare(loot, GameState.Actions(loot)).Automatic != null, "an unchanged selection screen is not a completed reward");
        policy.Observe(offer, skip, loot);
        Check(policy.Prepare(loot, GameState.Actions(loot)).Actions.Items().All(a => a["command"].Text("action") != "claim_reward"), "opened reward is remembered even without selection reward_id");
        loot["run"]!["floor"] = 7;
        Check(policy.Prepare(loot, GameState.Actions(loot)).Automatic != null, "a new floor starts a fresh reward sequence");
    }

    static async Task ShopRun()
    {
        using var handler = new Scenario(Shop());
        handler.Choose = body => Pick(body, "Replace potion slot 0");
        int gold = -1;
        handler.Apply = command =>
        {
            var state = handler.State;
            switch (command.Text("action"))
            {
                case "discard_potion": Discard(state, command); break;
                case "shop_purchase":
                    Check(command.Num("index") == 0, "purchase the offered potion");
                    var item = state["shop"]!["items"]![0]!;
                    AcquirePotion(state, item); item["is_stocked"] = false;
                    state["player"]!["gold"] = gold = 10; break;
                case "close_shop": state["shop"] = JsonNode.Parse("""{"items":[],"can_open":true,"can_proceed":true}"""); break;
                case "proceed": return Over();
                default: throw new Exception("Unexpected shop command");
            }
            return state;
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Win.", "run");
        await Wait(runtime);
        Check(handler.Requests.Count == 1 && gold == 10 && handler.Commands.Select(c => c.Text("action")).SequenceEqual(new[] { "discard_potion", "shop_purchase", "close_shop", "proceed" }), "one shop replacement decision, one purchase, then close the unaffordable inventory");
    }

    static async Task ShopBudgetQuestions()
    {
        var initial = Shop();
        initial["player"]!["gold"] = 362;
        initial["shop"]!["items"]!.AsArray().Add(JsonNode.Parse("""{"index":4,"category":"card_removal","price":100,"is_stocked":true,"can_afford":true}"""));
        using var handler = new Scenario(initial);
        JsonNode? back = null;
        var budgets = new List<int>();
        handler.Choose = body =>
        {
            var question = body["questions"]!["action0"]!;
            var instructions = question.Text("instructions");
            if (handler.State.Text("state_type") == "card_select")
            {
                Check(!instructions.Contains("available to spend at this shop"), "removal selection switches away from the shop question");
                return Pick(body, "Strike");
            }
            int gold = handler.State["player"].Num("gold")!.Value;
            budgets.Add(gold);
            Check(instructions.Contains($"You have {gold} gold available") && instructions.Contains("buy several items"), "wire question uses fresh gold and explains repeated purchases");
            Check(body.Text("state").Contains("Reserve at least 200 gold."), "shop requests retain player spending guidance");
            var criteria = question["criteria"]!.AsObject();
            Check(criteria.Any(p => p.Value!.ToString().Contains($"keep {gold}g")), "leaving remains available and describes the retained budget");
            if (gold == 362)
            {
                Check(criteria.Any(p => p.Value!.ToString().Contains("Remove a card from your deck") && p.Value.ToString().Contains("262g left")), "removal is explicitly identified with its price and remaining gold");
                Check(criteria.Any(p => p.Value!.ToString().Contains("Replace potion slot 0") && p.Value.ToString().Contains("312g left")), "compound potion purchases use the same budget");
                return Pick(body, "Remove a card from your deck");
            }
            if (gold == 262)
            {
                Check(criteria.Any(p => p.Value!.ToString().Contains("Cheap Card") && p.Value.ToString().Contains("232g left")), "later purchase options recalculate the remaining gold");
                return Pick(body, "Cheap Card");
            }
            Check(gold == 232 && criteria.Count > 1, "the provider may still leave with affordable purchases remaining");
            return Pick(body, "Finish shopping");
        };
        handler.Apply = command =>
        {
            var state = handler.State;
            switch (command.Text("action"))
            {
                case "shop_purchase":
                    var item = state["shop"]!["items"]!.Items().Single(i => JsonNode.DeepEquals(i["index"], command["index"]));
                    state["player"]!["gold"] = state["player"].Num("gold")!.Value - item.Num("price")!.Value;
                    item["is_stocked"] = false;
                    if (item.Text("category") == "card_removal")
                    {
                        back = state.DeepClone();
                        return new JsonObject { ["state_type"] = "card_select", ["player"] = state["player"]!.DeepClone(), ["card_select"] = JsonNode.Parse("""{"screen_type":"remove","cards":[{"index":0,"name":"Strike"},{"index":1,"name":"Defend"}]}""") };
                    }
                    return state;
                case "select_card": return back!;
                case "close_shop": state["shop"] = JsonNode.Parse("""{"items":[],"can_open":true,"can_proceed":true}"""); return state;
                case "proceed": return Over();
                default: throw new Exception("Unexpected budget test command: " + command);
            }
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Win. Reserve at least 200 gold.", "run");
        await Wait(runtime);
        Check(budgets.SequenceEqual(new[] { 362, 262, 232 }) && handler.Requests.Count == 4, "one request per shop/removal decision with updated budgets");
        Check(handler.Commands.Select(c => c.Text("action")).SequenceEqual(new[] { "shop_purchase", "select_card", "shop_purchase", "close_shop", "proceed" }), "removal, another purchase and voluntary exit keep their original commands");
    }

    static async Task InterruptedReplacement(bool stop)
    {
        using var handler = new Scenario(Shop());
        using var runtime = await Runtime(handler);
        handler.Choose = body => Pick(body, "Replace potion slot 0");
        handler.Apply = command =>
        {
            Check(command.Text("action") == "discard_potion", "interrupted replacement must never buy");
            Discard(handler.State, command);
            if (stop) runtime.Dispatch("POST", "/stop", null).GetAwaiter().GetResult();
            else throw new TaskCanceledException("uncertain discard outcome");
            return handler.State;
        };
        runtime.StartGameplay("Win.", "run");
        await Wait(runtime, stop ? "idle" : "error");
        Check(handler.Commands.Count == 1 && handler.Requests.Count == 1 && handler.State["player"].Num("gold") == 60, "stop or uncertain discard prevents the purchase and never retries");
    }

    static async Task DisabledStrategy()
    {
        using var handler = new Scenario(Rewards());
        handler.Apply = command => { Check(command.Text("action") == "proceed", "strategy off keeps the original model's reward decision"); return Over(); };
        using var runtime = await Runtime(handler, false);
        runtime.StartGameplay("Leave rewards.", "run");
        await Wait(runtime);
        Check(handler.Commands.Count == 1 && handler.Requests.Single()["questions"] == null, "reward automation is gated by Jev strategy");
    }

    static void RemoveReward(JsonNode state, int index)
    {
        var items = state["rewards"]!["items"]!.AsArray(); items.RemoveAt(index);
        for (int i = 0; i < items.Count; i++) items[i]!["index"] = i;
    }
    static void Discard(JsonNode state, JsonNode command)
    {
        var held = state["player"]!["potions"]!.AsArray();
        held.Remove(held.Items().Single(p => JsonNode.DeepEquals(p["slot"], command["slot"])));
    }
    static void AcquirePotion(JsonNode state, JsonNode item)
    {
        var held = state["player"]!["potions"]!.AsArray();
        var slot = Enumerable.Range(0, state["player"]!.Num("max_potion_slots")!.Value).First(i => !held.Items().Any(p => p.Num("slot") == i));
        held.Add(new JsonObject { ["slot"] = slot, ["id"] = item.Text("potion_id"), ["name"] = item.Text("potion_name") });
    }
    static string Pick(JsonNode body, string text) => body["questions"]!["action0"]!["criteria"]!.AsObject().First(p => p.Value!.ToString().Contains(text)).Key;
    static async Task<BotRuntime> Runtime(Scenario handler, bool enabled = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spire-buddy-jev-strategy-" + Guid.NewGuid().ToString("N"));
        var runtime = new BotRuntime(directory, "missing.dll", handler.Game, handler);
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["use_jev_strategy"] = enabled, ["api_endpoint"] = "https://buddy.test/v1" });
        return runtime;
    }
    static async Task Wait(BotRuntime runtime, string expected = "idle")
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonNode status;
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive") && status.Text("status") == expected, "strategy run failed: " + status["agents"].Text("last_report"));
    }

    sealed class Scenario(JsonNode state) : HttpMessageHandler
    {
        internal JsonNode State = state;
        internal List<JsonNode> Commands = [], Requests = [];
        internal Func<JsonNode, JsonNode>? Apply;
        internal Func<JsonNode, string>? Choose;
        internal IGameAdapter Game => new ScheduledGameAdapter(a => a(), () => State.DeepClone(), command =>
        {
            Commands.Add(command.DeepClone()); State = Apply!(command).DeepClone();
            return new JsonObject { ["status"] = "ok" };
        }, (_, _, _, _, _, _) => new JsonObject());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!; Requests.Add(body.DeepClone());
            JsonNode response;
            if (body["questions"] != null)
                response = new JsonObject { ["answers"] = new JsonObject { ["action0"] = new JsonObject { ["type"] = "choice", ["choice"] = Choose!(body) } } };
            else
            {
                var proceed = GameState.Actions(State).Items().Single(a => a["command"].Text("action") == "proceed");
                response = new SessionHandler("responses").Call("take_action", new JsonObject { ["snapshot_id"] = GameState.Fingerprint(State), ["action_ids"] = new JsonArray(proceed.Text("id")), ["message"] = "Proceeding.", ["rationale"] = "", ["plan"] = "" });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.WriteString()) };
        }
    }
}
