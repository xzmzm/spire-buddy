# Architecture

Spire Buddy runs inside Slay the Spire 2 as a Godot C# mod. The panel, gameplay
controller, model clients and static rule lookups share one process. The game
adapter schedules live reads and commands on Godot's main thread; model requests
run asynchronously. Installation and configuration are covered in
[the mod guide](../mod/README.md).

This document describes the 1.2.10 source synced from `codex/steam-release` at
`d8e9943`. That source includes changes after the currently published Workshop
package, which also carries the 1.2.10 version label.

```text
Game objects and controls (Godot main thread)
                 |
          ScheduledGameAdapter
                 |
       filtered public snapshot
                 |
      legal actions + decision brief
                 |
   automatic UI / model / Jev / solver
                 |
       validated action selection
                 |
     fresh snapshot and stop checks
                 |
       one command, then settle
```

## Code map

Paths below are relative to `mod/SpireBuddy/`.

| Area | Responsibility |
| --- | --- |
| `Mod.cs` and panel code | Mod entry point, chat, settings and UI updates. |
| `Game/GameBindings*.cs` | Read game objects and dispatch supported game commands. |
| `Runtime/IGameAdapter.cs` | Main-thread scheduling, cancellation and final command validation. |
| `Runtime/GameState*.cs` | Public-state filtering, legal actions, fingerprints and decision briefs. |
| `Runtime/ActionBatch.cs` | Resolve and validate deterministic action sequences and inline card choices. |
| `Runtime/BotRuntime.cs` | Chat and gameplay lifecycle, tool dispatch, decisions and traces. |
| `Runtime/AgentSession.cs`, `BriefMemory.cs`, `CompactState.cs` | History, bounded context, definition reuse and compact state rendering. |
| `Runtime/ModelClient.cs` | OpenAI-compatible Responses and Chat Completions transports. |
| `Runtime/EnemyDecompiler.cs`, `EventKnowledge.cs` | Static enemy and event rules from the installed game assembly. |
| `Runtime/Jev*.cs` | Optional TypeSafe choice API, decision criteria and uncertainty review. |
| `Game/CombatSolverBridge.cs` | Optional Combat Solver handoff. |

## Public state and legal actions

The controller builds a detached public snapshot before each decision. A recursive
filter removes seeds, RNG state, draw/shuffle order, future reward rolls, hidden
encounters and future rolled enemy moves. Current intents, pile contents without
draw order, visible map topology and boss, and static game rules remain available.

A snapshot ID is the first 16 hexadecimal characters of SHA-256 over canonical
public JSON. Each legal action has a short model-facing ID and a controller-owned
command. The model chooses an advertised ID and echoes the snapshot ID; generated
text is never executed as a game command.

Gameplay briefs include the current state, legal actions, instructions and plan.
Hand cards have one-based aliases such as `c1`; targeted plays use `cN@enemy`.
Playable aliases and targets are grouped without repeating card names. Pile cards
and glossary definitions are deduplicated. Read-only inspection tools still return
the full public JSON.

A decision can contain up to 32 deterministic steps. Inline choices such as
`["c3 c1", "a5"]` play the original c3, choose the original c1 for its hand-selection
effect, confirm locally, then continue to a5. Process-local card identities keep
identical copies distinct when hands reorder. Every step resolves against fresh
legal actions. Draws, random effects, unexpected changes or unavailable actions end
the batch; explicit close, confirm, proceed and end-turn actions are terminal.

The controller handles routine transitions locally: merchant entry, completed
selections, mechanical dialogue, exhausted shops, a single map path and proceeding
when no strategic choice remains. `auto_treasure` defaults on and opens chests and
claims their revealed relics. An active Winged Boots charge keeps map choices under
model control, even when only one ordinary path is available.

Crystal Sphere uses the shared `CrystalSphereView` geometry layer. Only visible
fragments contribute item information. Briefs show both tools' coverage and a lower
bound on completion cost; unknown cells remain unknown. Each tool change or reveal
is a separate decision, and the numeric divination counter gates legal clicks.

Orb briefs explicitly say that the first listed orb is the rightmost orb and the
next to evoke. Dualcast evokes that orb twice.

## Gameplay decisions and model compatibility

The regular gameplay model can inspect public state, legal actions, discovered
items, recent transitions, enemy rules and event rules. It submits a decision with
`take_action`, alone after any inspections. Inspection calls execute serially; a
`take_action` bundled with inspections is rejected with validation feedback.
Submitting the tool does not itself mutate the game: the controller validates and
executes the selected commands afterward.

`ModelClient` supports Responses and Chat Completions. Gameplay calls pass
`requireToolCall: true`, which sends `tool_choice: "required"`. Ordinary chat and
connection tests leave tool choice unforced. Strict tool schemas constrain the
arguments, while controller-side checks still validate the selected action.

If a successful response contains only text or no tool call, the controller reads
the live state again. Changed state gets a fresh decision brief. An unchanged
snapshot permits two corrective requests, for three attempts in total, before
stopping with the last explanation. Prose is never converted into an action.
`missing_tool_call` traces record the explanation and whether the state changed.

This source has no automatic fallback for providers that reject forced tool choice.
Such an HTTP error occurs before the missing-tool-call recovery path, so a working
chat or connection test does not establish gameplay compatibility. The configured
gameplay endpoint must accept the required tool-choice request.

Tool rounds are bounded to 24 per decision, and a started play is bounded to 2,000
decision-loop steps. Context and response validation failures also have bounded
recovery paths. None of these retries replays an already submitted game mutation.

## Enemy and event rule lookup

Both knowledge tools use ILSpy in process against the installed `sts2.dll`.
They read static source without creating events, rolling rewards, or inspecting
live hidden state. Assembly-hash cache keys prevent stale rules after a game update.
Lookup work runs outside the main-thread adapter; execution still checks freshness
after the lookup and model response.

`lookup_enemy_moves` resolves an exact monster class first, then up to five partial
matches. It has a 15-second deadline and a 24,000-character source limit per match.
Conditional notes explain `RandomBranchState.AddBranch` overloads so cooldowns and
repeat limits are not confused. Failures are returned as lookup errors.

`lookup_event` resolves a stable event ID or English name and follows source
references to cards, relics, powers, pools and event helpers. Ambiguous names return
candidates. A bundle is limited to 15 seconds, 12 sources, 24,000 characters per
source and 72,000 characters total. Missing or truncated sources explicitly mark
partial coverage; successful bundles use a versioned, assembly-hash cache. Dynamic
engine behavior and localization are not evaluated by the source traversal.

Before a strategic event decision, both regular gameplay and Jev receive the
current event's rule bundle. Regular histories retain an unchanged bundle and
restore it after context rollover; stateless Jev receives it on every evaluation.
Briefs include the current stage, visible option keys, full deck rules, held items
and reachable public map context. Shared instructions compare remaining costs and
alternative exits; earlier HP or gold costs do not justify continuing by themselves.
Chat also exposes explicit event lookup.

## Sessions, compact state and context

Buddy is the persistent chat session. Its serial message queue preserves complete
assistant, tool and reasoning transcripts. It coordinates at most one strategy
session and one combat session through `start_game`, `message_agent`, `stop_game`
and `inspect_agents`. Chat has read-only knowledge tools and no `take_action`.

One gameplay worker owns all execution. Strategy handles menus, navigation, events,
shops and rewards; an active combat session owns combat and its card selections.
Strategy waits for combat, and each new fight gets a fresh combat session. Run scope
normally ends at game over; fight scope ends before rewards or navigation. The
player sees one Buddy voice and can keep chatting while gameplay runs.

Buddy relays relevant guidance to gameplay rather than copying every chat message.
New guidance invalidates an in-flight decision. A literal `stop` or `停` cancels play
immediately without a model call. A control epoch prevents earlier queued or
in-flight chat responses from restarting play afterward. Conversation history
survives stopping.

`CompactState` renders repeated object arrays with a column header and rows:

```text
nodes[2]{col,row,type,children}:
  [4,2,"Unknown",[[4,3],[5,3]]]
  [4,3,"Monster",[[5,4]]]
```

The format preserves array positions, card variants, JSON escaping, false values,
nulls and empty collections. `~` or omitted trailing cells mean an absent property;
`*5 [values]` means five consecutive identical objects. A legend accompanies full
anchors. Ordinary JSON is used when it is smaller, including the legend cost.
Canonical snapshots, fingerprints, tool results and diff baselines remain JSON.

Chat omits unchanged state and sends JSON Pointer `set`/`remove` changes when smaller
than a full replacement. Nested patch values can use compact tables. Tool rounds
refresh the diff baseline, and play-status changes re-anchor the full state. A
finished play's report is queued in conversation order for the next user turn.

Gameplay histories reuse unchanged instructions, deck and relic definitions,
glossary entries and route adjacency. Tactical state, legal actions and snapshot
IDs remain present. New histories receive complete context; stateless Jev briefs
never depend on previous omissions.

`AgentSession` tracks a stable prompt-cache identity, history and token usage.
Responses uses `store = false` and replays encrypted reasoning, calls and results;
Chat Completions replays the equivalent message protocol. Stable instructions and
tool schemas lead the history, and `prompt_cache_key` stays stable until rollover.

A conservative UTF-8 estimate and reported usage bound context. On overflow, chat
restarts with current state and completed conversation, then tries state with only
the latest message, then state alone. Gameplay restarts with current state, legal actions,
instructions, plan and event rules. Whole tool protocols are retained or discarded
together, preventing orphaned results. Unfit state produces an error after bounded
retries. Endpoint, model or API-format changes rebuild compatible history; effective
personality changes append instructions to the existing conversation.

## Optional Jev and Combat Solver paths

`use_jev_strategy` and `use_jev_combat` independently select TypeSafe's choice API
for the corresponding decisions. Jev has its own endpoint, model and API key. Chat
continues to use Buddy. Combat Solver is checked before either combat provider,
including combat-owned modal choices; an unavailable or refusing solver falls
through to the configured provider.

Each Jev evaluation is stateless and includes the current public brief and typed
choice criteria. Groups contain at most 255 options, with a final comparison when
needed. Returned IDs must belong to their group. The selected command then follows
the same freshness, cancellation and execution checks as a regular model decision.
HTTP 429/529 evaluations retry at most twice with backoff; mutations are not retried.

Jev's brief includes deck dependencies, upgrade previews, public enemy/orb effects,
route distances, recent actions and a small last-fight memory. Dependency scans
remain incomplete evidence, and visible attacks minus Block are not represented
as exact future HP loss.

`JevStrategy` sequences routine reward handling and builds potion replacement
choices. Replacements use controller-owned discard and claim/purchase steps;
after discarding, the controller checks the changed player state and re-resolves
the next command. Shop and rest-site questions use the current budget and actual
available choices. Unknown reward types stay model-controlled.

Optional `jev_review_uncertain` defaults off. When enabled, uncertainty or selected
visible combat facts can trigger one isolated Buddy review that can choose only
a current legal ID. It cannot execute actions or start another review. Both
original and reviewed choices are traced before normal freshness validation.

## Execution and settlement

`ScheduledGameAdapter` is the boundary between background work and game objects.
Immediately before every mutation, on the game thread, it checks cancellation and
guidance, reads fresh public state, compares the expected fingerprint, and verifies
that the command is still legal. It then invokes that command once. Cancelled or
timed-out queued operations are skipped if a later frame drains the queue.

After dispatch, the controller polls for a changed, ready state. Loading frames,
empty/discard-only screens and input-blocked shops wait within the settle timeout.
Merchant bindings tolerate events that have not finished initialization. Dialogue
positions and event option keys distinguish consecutive identical-looking lines.
Player-opened overlays wait for the player to close them and remain cancellable.

An interactive screen that stays unchanged after settlement is a definitive no-op:
the outcome reports `executed=false`, and a new decision can choose again. Three
consecutive ineffective clicks on one snapshot stop play. An uncertain mutation
outcome stops immediately to prevent duplicate commands.

The next decision returns per-action outcomes and a lightweight legal-action hint
to the same model conversation, followed by a fresh state brief. Full before/after
snapshots are not embedded in those outcomes. `list_legal_actions` always reads
live state; an inspection that finds changed state restarts the decision with the
correct strategy or combat owner.

## Persistence and verification

Settings, assembly-keyed rule caches and run traces live under
`user://spire-buddy/`. Each started or resumed play writes a new `runs/*.jsonl` log.
Traces include usage, decisions, stale-state and missing-tool-call records, outcomes,
solver handoffs, stops and errors; they omit full before/after snapshots and
credentials. Each record has a local ISO 8601 `timestamp` and monotonic `elapsed_ms`
for ordering and latency diagnostics. Commentary is timed when emitted; outcomes
are timed after settlement.

The .NET 9 native suite exercises both model transports with fake HTTP services
and adapters. It covers public-state filtering, validation, compact state, context
rollover, stop/steering races, bounded recovery, optional providers and event rules.
`-- --events` and `-- --compact-state` select the new rule and state checks.
`STS2_GAME_ASSEMBLY` additionally enables installed-game enemy and event source
checks. Commands and other selectors are in [Verification](../mod/README.md#verification).
Live Godot bindings, mod-loader integration and the panel still need an installed
game smoke test after a restart.
