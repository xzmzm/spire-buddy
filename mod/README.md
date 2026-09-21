# Spire Buddy — self-contained game mod

Spire Buddy runs its C# agent controller inside Slay the Spire 2. It includes the
model client, persistent Buddy/game/combat sessions, public-state filtering,
legal-action enumeration, freshness validation, action batches, operator chat,
settings, and JSONL traces. The mod reads live game objects and dispatches actions
on Godot's main thread, while card and relic search uses the game's own model
registry.

The mod sends requests directly to the configured OpenAI-compatible HTTP endpoint.
It does not launch a helper process, local web server, browser dashboard, or
decompiler executable, and it does not include the game's assemblies.

## Requirements

- A working Slay the Spire 2 mod-loader installation.
- The .NET 9 SDK to build the mod and the .NET 9 runtime to run tests.
- A game data directory containing `sts2.dll` and `GodotSharp.dll`.
- Python 3 only when using the cross-platform Python installer.

## Build and install

The mod ID and assembly name are `SpireBuddy`; the display name is **Spire Buddy**.
The installer builds against the game's assemblies and copies `SpireBuddy.dll`,
`ICSharpCode.Decompiler.dll`, `SpireBuddy.json`, and the third-party
notices to the game's mod directory.

On macOS, the shell installer uses the bundled arm64 game-data layout:

```sh
./scripts/install-mod.command
./scripts/install-mod.command "/path/to/Slay the Spire 2"
```

On Linux, pass the game installation directory explicitly:

```sh
./scripts/install-mod.command "/path/to/Slay the Spire 2"
```

On Windows, use the PowerShell installer. Without `-Game` it searches the
Steam libraries registered on the machine:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-mod.ps1
powershell -ExecutionPolicy Bypass -File scripts\install-mod.ps1 -Game "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

The installers look for `sts2.dll` in the game's data directory and install
to `Contents/MacOS/mods` for a macOS app bundle or `<game>/mods` on other
platforms; the Python installer remains available cross-platform. If an older `STS2Bot` installation is present,
its files are moved to recoverable backups so both mod identities are not loaded.
Existing installed files are replaced through a staged operation with recoverable
hard-link backups. The mod preserves `user://spire-buddy/` settings, panel layout,
cache, and run traces. Restart the game after installation, enable **Spire Buddy**,
and never distribute the game's assemblies.

For a build only, pass the data directory directly:

```sh
dotnet build mod/SpireBuddy -c Release -p:STS2GameDataDir="/path/to/game/data"
```

The path must contain both `sts2.dll` and `GodotSharp.dll`. On Windows,
the equivalent PowerShell command is:

```powershell
dotnet build .\mod\SpireBuddy -c Release -p:STS2GameDataDir="C:\path\to\data_sts2_win64"
```

## Configure and use

**Settings** starts with collapsed sections for **Buddy's personality**,
**Buddy model & connection**, **Jev decisions**, **Gameplay automation**, and
**Context limit**. Expand a heading to edit it; **Save settings** stays visible
below the scrolling form. Connection tests use the values currently in the form.

Set Buddy's endpoint, model, API format, and reasoning effort under **Buddy model
& connection**. Its endpoint is a base URL, so include the provider's API prefix,
such as `https://api.openai.com/v1`.

When no saved settings or environment overrides exist, the first-run defaults are:

| Setting | Default |
| --- | --- |
| API endpoint | `http://localhost:8317/v1` |
| Model | `gpt-5.6-sol` |
| API format | `responses` |
| Personality | Witty streamer (`witty_streamer`) |
| Max context tokens | `250000` |
| Use Combat Solver | Off |
| Hide Combat Solver UI | Off |
| Auto loot treasure chests | On |
| Use Jev for strategy / combat | Both off |
| Review uncertain Jev choices | Off |
| Jev evaluation URL | `https://api.typesafe.ai/v1/systemone` |
| Jev model | `jev-latest` |

If `STS2_BOT_BASE_URL` or `STS2_BOT_MODEL` is set, it seeds the
corresponding first-run value. Replace the local endpoint and default model unless
that endpoint is intentional.
Use `responses` with a Responses-compatible provider or
`chat_completions` with a provider that implements `/chat/completions`.
**Test Buddy connection** and model discovery use the unsaved form values. If the key
field is untouched, the stored key is used. Save only while play and pending Buddy
replies are idle.

Personality choices have English and Chinese labels. Choose **Custom** to write
your own voice in a multiline editor; it starts with a localized example when no
description is saved. A blank description uses that example as the default.
The description is saved across restarts, stays available when you switch to a
preset, and applies to both chat and gameplay commentary. Language changes retain
unsaved settings, including your custom description.

Saving a personality change adds an instruction to Buddy's next chat turn while
preserving the existing prompt, history, and cache key. Fresh chat, game, and
combat sessions use the latest saved personality in their initial prompt.
Unchanged settings and updates to reasoning effort, context limit, or API key
also preserve Buddy's session. Changing the endpoint, model, or API format
starts a fresh provider session with completed chat text; context overflow
still uses the normal rollover behavior.

The API key is stored in the game's `user://spire-buddy/settings.json` user-data
directory and survives restarts. Treat that file as sensitive. The key field shows
masked asterisks while a key is stored; type a new key to replace it, or leave the
mask untouched to keep the stored key. Agent sessions and the visible chat feed
are in memory for the current game process.

### Jev decisions

Expand **Jev decisions** to configure a full evaluation URL, model ID, and separate
API key, then use **Test Jev connection**. The default URL follows the
[TypeSafe API reference](https://docs.typesafe.ai/api); a compatible custom URL is
used exactly as entered, with no path appended. The Jev key persists and is masked
independently of Buddy's key. Blank or unchanged masked fields keep saved keys.
Jev connection testing works even while its decision toggles are off.

- **Use Jev for strategy** replaces the game/map model's decisions for menus,
  paths, events, rewards, shops, rest sites and other campaign choices.
- **Use Jev for combat** replaces the combat model, including card-selection
  screens within a fight. An enabled, available Combat Solver has priority.
  Jev handles a fight if the solver is off, missing or refuses control.
- **Review uncertain Jev choices** asks the configured Buddy model for one
  independent choice when Jev is uncertain, its leading choices are nearly tied,
  or it proposes a potentially wasteful end turn or zero-Block Fortifier.
  This is off by default. Enable it to request a second opinion, with extra latency
  and API cost. Reviews retain the same stop, guidance and stale-state checks.

Enable both to use Jev for all model-controlled gameplay. Buddy's chat continues
using **Buddy model & connection**, including starting, stopping and relaying
strategy updates. Automatic navigation and treasure handling still avoid model
requests. With a Jev toggle off, the corresponding existing gameplay model is used.

With **Use Jev for strategy** enabled, rewards follow this order:

1. Take gold automatically, then open card rewards.
2. Let Jev choose a card or skip each card reward, one after another. Each reward
   is tracked separately, so a skipped offer cannot reopen endlessly or hide a
   second offer with the same label. Tracking survives Stop/Play in the same room.
3. Back on the reward screen, take relics automatically.
4. Take potions automatically while slots are free. With a full inventory, Jev
   chooses **replace potion slot N** (discard that held potion, then take the
   offered potion) or **skip this potion**. Each remaining potion is considered
   separately, so skipping one does not skip the others.
5. Proceed automatically when rewards have been handled. Skipped cards/potions stay
   on the game screen until proceeding leaves them behind.

Jev's shop brief and choices include only stocked items priced at or below current
gold, with their prices. Full potion slots replace the ordinary potion purchase
with one replacement option per held slot; Jev can also buy another item or leave.
Affordability is refreshed after every purchase. Each replacement settles the
discard, rechecks the specific offered item and price, and only then claims or
buys it. A stop, uncertain discard, or unexpected state change interrupts the
remaining step. These reward and replacement rules apply only to Jev strategy.

The shop question states the exact current gold balance and asks Jev to compare
useful combinations of purchases, removal, and saving for a concrete need. Each
purchase choice shows the gold left afterward; card removal and finishing the
shop have explicit descriptions. Buying one item keeps shopping open for another
decision, with the budget and affordability refreshed each time. Player spending
instructions remain part of every request.

Rest sites use a dedicated question stating current and missing HP. At full HP,
the Rest choice explicitly says it restores **0 HP**, and the question favors a
useful upgrade unless a visible non-healing effect or player instruction justifies
resting. When injured, Jev weighs recovery against the value of an upgrade and
upcoming fights. Smith explains that the permanent upgrade is selected next;
irrelevant potion discards are omitted. Rest and other enabled options stay
available, including when Smith is disabled or relics change their value.
Actual before/after card upgrade previews are included at the rest site. Reward
questions compare a card with keeping the current deck, while upgrade questions
compare the improvement itself, including cost and opening-hand effects.

Each Jev request contains a fresh public brief and a typed choice among current
legal actions. It includes player instructions, HP/resources, unique card rules,
deck/pile composition, relics and counters, potion slots, visible options and
upgrade previews, reachable map topology/boss for strategic choices, and combat
intents/effects. Route summaries show distances to recovery/shops and elite
exposure; combat omits those route calculations and deck-building scans. Combat
choices show current Block (including zero), visible incoming damage, and fresh
target-specific attack previews from the game's own calculation hooks. Target
damage includes current modifiers but precedes Block and HP-loss/death effects;
the prompt distinguishes per-hit previews from complete attack outcomes. Repeated cards and mechanic
descriptions are compacted; four recent action summaries, observed HP/gold changes,
last-fight HP/potion counts and any failure feedback are carried forward. There is no growing chat history,
tool schema, or hidden RNG information in the request. Jev chooses one action at a
time (a potion replacement includes its discard and pickup), then receives the
settled state for its next decision. Commentary describes
the selected action locally; Jev's choice API does not generate a prose rationale.

With Jev combat enabled, a small local check first looks for a verified attack
sequence that kills every enemy this turn. It supports the starting Strikes,
Bash, Twin Strike, Unrelenting and Perfected Strike with audited combinations of
Strength, Weak, Vulnerable, Slow, Vigor and free-attack effects. It accounts for
Block, energy/stars, attack order and consumed bonuses, and requires its damage
arithmetic to match native target previews. Unknown hooks, death phases, card
modifications, multiplayer and incomplete state disable the check. It does not
guess draws, random outcomes or potion combinations. Only the first legal action
executes; the next settled snapshot is checked again. These decisions and their
proof steps use `jev_lethal` trace events and make no model/review request. Other
positions use Jev's combat question, which now checks kills before unnecessary
Block. Optional Buddy review remains off by default.

Every selected action passes the same freshness, guidance and stop checks as
Buddy's decisions. Invalid answers stop play; rate limits and overloads have
bounded retries. Screens with over 255 legal options use grouped comparisons
followed by a comparison of the winners. **Max context tokens** also bounds each
complete Jev request; an oversized request stops with a settings hint rather than
silently omitting choices or state. Token usage is recorded in `jev_response`
trace events, together with full confidence/probability answers, model identity and
prompt version. Reviews and executed outcomes link back to the decision snapshot.
Confidence is a measure of the option distribution, not a win probability; the
review thresholds are initial heuristics, not calibrated measures of playing skill.

### Combat Solver integration

**Settings → Gameplay automation** offers two optional toggles for the [Combat Solver](https://steamcommunity.com/sharedfiles/filedetails/?id=3790899961)
workshop mod (战斗路线求解器). Both grey out with an install hint while that mod
is missing, and wake up once its assembly has loaded:

- **Use Combat Solver** — Buddy hands each combat to the solver's
  full-auto mode instead of playing fights with its own combat model. Combat
  choices, including Choices Paradox before the first turn, stay under the
  solver's control so its planned card selection and route are preserved. The
  solver is armed at every combat start and re-armed whenever it stops itself
  mid-fight, so it auto-plays the whole combat; Buddy resumes on the next
  non-combat screen. If the solver is unavailable, disabled in its own
  settings, or refuses the take-over, Buddy falls back to playing the fight
  itself and says so in the feed. Saying **stop** also disarms the solver.
- **Hide Combat Solver UI** — the solver's overlay normally
  appears during combat; with this on, Buddy keeps it hidden for the whole
  fight. The toggle acts whenever combat is in progress, whether or not Buddy
  is playing.

Talk to Buddy in the single chat composer. Questions do not start play by
themselves. The initial chat window includes these examples:

| Example | Behavior |
| --- | --- |
| `Win with ironclad` | Start play for the run. |
| `Continue this run` | Continue play through the run. |
| `Win this fight` | Play only the active combat, then stop before rewards or navigation. |
| `What should I do next?` | Ask for advice without starting play. |
| `What are my potion reward chances?` | Explain the current public potion odds. |
| `What moves can this enemy use?` | Look up static rules from the installed game assembly. |
| `What should I pick from these rewards?` | Compare visible reward choices. |
| `Which card, relic, path, or shop option is best?` | Compare visible choices. |

Say **stop** (or **停**) to cancel play immediately; Buddy remains available for conversation.
While playing, send a strategy update and Buddy will reconsider pending decisions.
Run strategy, combat, rewards, shops, events, and navigation use private runtime
context, but the player always sees one Buddy voice.

Each runtime session uses **Max context tokens** independently. Buddy's rollover
tries the current state plus previous chats, then the state plus the latest message,
then state alone. Game and combat rollovers keep current state, instructions, and
plan. Estimates include tool schemas and reported usage; provider context-limit
errors use the same bounded rollover. If the minimum context still does not fit,
increase the limit in Settings. The feed survives rollovers and settings changes.

Drag the header to move the panel, the corner grip to resize, and **−** to collapse.
Position and expanded size are saved in `user://spire-buddy/panel-layout.json` after
moving or resizing and on exit. The panel restores them on startup, stays within
the game view after resolution changes, and follows new messages until you scroll
up. Expand a message's chevron to read its rationale.

Buddy replies support headings, **bold**, *italic*, `code`, ~~strike~~, bullet and
ordered lists, quotes, fenced code blocks, tables, and HTTP(S) links. Fonts follow
the game's locale. Network calls and decompilation run off the game's main thread;
UI changes return to the main thread.

## Localization

The panel follows the game's language setting: Chinese (simplified or
traditional) renders the panel in Chinese, and every other language falls back
to English. The language is read from the persisted settings save — not the
transient `LocManager` value the game flips to English while uploading
metrics — so a change re-renders the panel without restarting the game. The
**stop** shortcut recognizes `stop`, `停`, and `停止` with trailing punctuation;
anything else still routes through Buddy, who can stop play in any language.
With the Chinese interface, fresh chat, game and combat sessions include a
language directive in their prompt. Switching between Chinese and English
mid-session appends a language-change note to the live histories, preserving
their prompts and provider cache keys. Game and combat sessions check before
every model request, including tool follow-ups; the game session also receives
changes while waiting for combat to finish.

Game data stays language-independent where it matters: entities expose stable
registry IDs alongside localized display names, enemy lookups key on the
registry ID, and model briefs render whatever names the game displays.

## Decompilation and safety

Enemy lookup uses ILSpy's `ICSharpCode.Decompiler` library **in process**, reading
`sts2.dll` as static metadata/source. It resolves exact monster types first,
falls back to up to five partial matches, truncates each source result to 24,000
characters, and stops a lookup after 15 seconds. It never invokes `ilspycmd`, an
EXE, a shell, or a `dotnet` child process. Results are cached under
`user://spire-buddy/` using the assembly SHA-256, so a different game build
cannot reuse stale source. A lookup error is reported as missing knowledge.
If the returned source calls `RandomBranchState.AddBranch`, the lookup adds
compact notes covering its overloads, parameter meanings, and defaults.

Before model packets are built, a recursive information firewall removes seeds,
random-number state, draw or shuffle order, future rewards or encounters, and
already-rolled future enemy moves. Current intents, pile membership without order,
the visible map and boss, and static rules remain available. Each decision reaches
the model as a compact line-based brief with aggregated piles, a history-scoped
keyword glossary, a 16-hex snapshot id, and the current legal actions. The
uncompressed public JSON remains available through inspection tools.

The controller selects only from locally enumerated legal actions and checks the
selection against a fresh, settled snapshot. A batch can contain up to 32 steps
on any screen. Each queued step is re-resolved against fresh state. Close,
confirm, proceed, and end-turn actions must be last.

Combat briefs label hand cards `c1`, `c2`, … (one-based). Use those IDs to play
cards, or `c1@enemy` for an advertised target; a bare card ID also works when only
one target is legal. Each `action_ids` entry can append known discard/exhaust
choices: `["c3 c1", "a5"]` plays c3, selects c1, confirms, and executes a5
(if a5 is the advertised end turn). `["a3 c1", "a5"]` also works when a3 is
the play action. Multiple choices fit in one entry: `"c3 c1 c2"`.
All card references bind to the initial snapshot, so preceding plays and selections
may shift indices without changing which card is chosen, including identical copies.
On an existing hand-selection screen, `c1` selects its first listed card.

Inline choices and their implicit confirmation count toward the 32-step limit.
Only the expected hand-selection detour can resume the batch. Selection limits,
missing cards, draws, new information or other screen changes stop it for a fresh
decision; confirmations still require a currently legal action.
Shop purchases, reward claims, and selection toggles may be grouped when the
result remains deterministic; potions are used or discarded one at a time.
Shop card removal, new information that changes queued choices, and other screen
transitions end the sequence. The controller validates and executes every action
individually.

Combat plans include end turn after the last worthwhile deterministic play. If the
model omits it, the controller can append end turn after a verified card batch
when only end turn and potion discards remain, avoiding another model call.
Snapshots with no actions except potion discards keep polling through the room
animation until a choice or proceed button becomes available, within the normal
30-second settle timeout. Rest, rewards, and other rooms therefore return their
completed actions without an extra model inspection call.
Routine transitions are handled locally when no strategic choice remains: opening a
merchant, closing an empty or exhausted inventory, confirming a completed card or
bundle selection preview, advancing a mechanical dialogue step, proceeding from a
room, or taking a single map path. With **Auto loot treasure chests** on (the
default), treasure rooms run entirely without a model call: the chest is opened,
each revealed relic is taken, and the room proceeds. An active Winged Boots charge keeps map travel
model-controlled so its path-breaking choice is not spent accidentally; an
exhausted relic does not block the shortcut. Playable cards, usable potions, draws,
random effects, and unexpected state changes still require a decision.

Normal and fake merchants both open automatically with Jev or the regular
gameplay model. During fake merchant setup, Buddy waits for the initialized local
event and its controls before opening the inventory. Purchases remain strategic;
closing a completed shop proceeds without opening it again.

A rejected selection is returned to the gameplay session so it can re-decide;
three consecutive validation failures stop play. Mutations are never retried,
including after a timeout. Uncertain or unchanged outcomes stop the controller.
Model requests are bounded to 24 tool rounds per decision and 2,000 decision steps
per start.

When `list_legal_actions` has no suitable move or only potion discards, the
controller waits 600 ms once and reads the live snapshot again before the agent
decides whether to stop. Each started play writes JSONL to
`user://spire-buddy/runs/`. Traces contain runtime events, model usage,
stale-decision records, and per-action outcomes; they do not contain full
before-and-after public snapshots.

Gameplay briefs omit unchanged instructions, decks, and relic rosters within each
model history. Changed decks and relic rosters replace the previous roster,
including empty ones. Relic counter changes are reported without repeating known
descriptions. Potions keep current slot/name listings, report removals as “no longer
held”, and include descriptions only when new or changed. Keyword definitions follow
the same rule. Game and combat histories track this context separately; every new
combat or context reset receives a full briefing. Tactical state, card text, plans,
snapshot IDs, and legal actions remain present each turn. Card plays share the
brief's card IDs instead of repeating card names in each action. Hand-selection
briefs render only the selectable cards and chosen names, avoiding a second,
conflicting hand index list. Public inspection JSON retains full card details.

Crystal Sphere briefs show a coordinate board, distinguish visible fragments from
completed rewards, and list each known item's missing cells. Small clears one
cell and big clears the centered 3×3; either costs one divination, while switching
tools is free. The brief includes both tools' candidate previews and a geometric
lower bound on the clicks needed to complete an item, flagging impossible finishes
within the remaining budget. Every legal click's inspection summary reports newly
cleared cells, unknown exposure, item progress, and known reward/curse completions.
Unknown exposure is never labeled safe. The same information reaches Jev.

Only items with a visible fragment are described; actual reward rolls stay unknown.
Tool changes and reveals are submitted individually so the model can reassess each
new board. The numeric divination counter gates both legal actions and native
execution. At zero, Buddy waits for rewards or the enabled proceed button rather
than treating still-visible cells as clickable.

## Verification

Build and run the native test project:

```sh
dotnet build mod/SpireBuddy.Tests -c Release
dotnet run --project mod/SpireBuddy.Tests -c Release
```

For just the compact card-sequence checks, append `-- --card-choices` to the run
command. Use `-- --dialogue` for ancient and Architect dialogue continuation and
settlement, or `-- --solver` for Combat Solver hand-offs, card choices, and fallbacks.
Use `-- --jev` for Jev request/response validation, compact state, independent
credentials, decision routing, solver priority, and stop/steering checks.
Use `-- --jev-combat` for the missed-lethal regression positions, sequencing,
native-preview agreement, multiple targets and unsupported-effect fallbacks.
Use `-- --jev-strategy` for ordered reward handling, potion replacement/skip,
affordable shop choices, updated shop budgets and removal/exit decisions, and
interruption between replacement steps.
Use `-- --non-combat` for shared non-combat batches and adapter execution checks.
Use `-- --merchant` for normal/fake merchant loading and automatic entry with
Jev and the regular gameplay model, followed by purchases and leaving the shop.
Use `-- --crystal` for Crystal Sphere fragment/completion regressions, tool geometry,
curse overlap, hidden-information isolation, action mapping and final-click settlement.

The tests target `net9.0` and need the .NET 9 runtime. They need neither
Godot nor Python. The suite exercises both model transports with fake HTTP
services, legal actions, hidden-information filtering, stale-action rejection,
uncertain mutations, session persistence, context rollover, and stop/steering
races.

To also exercise the ILSpy decompiler against the installed game assembly:

```sh
STS2_GAME_ASSEMBLY="/path/to/sts2.dll" dotnet run --project mod/SpireBuddy.Tests -c Release
```

In PowerShell:

```powershell
$env:STS2_GAME_ASSEMBLY = "C:\path\to\sts2.dll"
dotnet run --project mod/SpireBuddy.Tests -c Release
```

The native UI and mod-loader integration require a game restart and a smoke test
after installation. The test project does not validate the live game bindings.
