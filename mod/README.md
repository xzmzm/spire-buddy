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

Set the endpoint, model, API format, reasoning effort, personality, and **Max
context tokens** in **Settings**. The endpoint is a base URL, so include the
provider's API prefix, such as `https://api.openai.com/v1`.

When no saved settings or environment overrides exist, the first-run defaults are:

| Setting | Default |
| --- | --- |
| API endpoint | `http://localhost:8317/v1` |
| Model | `gpt-5.6-sol` |
| API format | `responses` |
| Personality | Witty streamer (`witty_streamer`) |
| Max context tokens | `250000` |
| Use Combat Solver to auto fight | Off |
| Hide Combat Solver UI during combat | Off |

If `STS2_BOT_BASE_URL` or `STS2_BOT_MODEL` is set, it seeds the
corresponding first-run value. Replace the local endpoint and default model unless
that endpoint is intentional.
Use `responses` with a Responses-compatible provider or
`chat_completions` with a provider that implements `/chat/completions`.
**Test connection** and model discovery use the unsaved form values. If the key
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

### Combat Solver integration

**Settings** offers two optional toggles for the [Combat Solver](https://steamcommunity.com/sharedfiles/filedetails/?id=3790899961)
workshop mod (战斗路线求解器). Both grey out with an install hint while that mod
is missing, and wake up once its assembly has loaded:

- **Use Combat Solver to auto fight** — Buddy hands each combat to the solver's
  full-auto mode instead of playing fights with its own combat model. The solver
  is armed at every combat start and re-armed whenever it stops itself
  mid-fight, so it auto-plays the whole combat; Buddy resumes on the next
  non-combat screen. If the solver is unavailable, disabled in its own
  settings, or refuses the take-over, Buddy falls back to playing the fight
  itself and says so in the feed. Saying **stop** also disarms the solver.
- **Hide Combat Solver UI during combat** — the solver's overlay normally
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
room, or taking a single map path. An active Winged Boots charge keeps map travel
model-controlled so its path-breaking choice is not spent accidentally; an
exhausted relic does not block the shortcut. Playable cards, usable potions, draws,
random effects, and unexpected state changes still require a decision.

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

## Verification

Build and run the native test project:

```sh
dotnet build mod/SpireBuddy.Tests -c Release
dotnet run --project mod/SpireBuddy.Tests -c Release
```

For just the compact card-sequence checks, append `-- --card-choices` to the run
command. Use `-- --dialogue` for ancient dialogue continuation and settlement.

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
