# Native runtime

`mod/SpireBuddy/Runtime` owns the controller and talks to the model endpoint. The
Godot panel dispatches in-process calls; it does not use an HTTP control server.
`mod/SpireBuddy/Game` reads the live game objects, filters observations, enumerates
commands, and dispatches clicks or game commands on the main thread.
`GameState` filters observations and enumerates commands, `ActionBatch`
validates/re-resolves batches, `ModelClient` handles the two API formats, and
`EnemyDecompiler` reads static assembly source through ILSpy's DLL.
`BotRuntime` owns lifecycle, settings, session history, operator chat and traces.
`JevClient` provides an optional TypeSafe choice API for strategic and combat
decisions; `GameState.Jev` builds its self-contained public brief.
`JevStrategy` applies reward sequencing and builds potion replacement choices.
See [the native guide](../mod/README.md) and the executable native tests.

# Architecture

```text
Slay the Spire 2
       │ live objects / Godot controls
       ▼
in-process game adapter (main thread)
       │ public structured state
       ▼
legal action enumerator ──► public snapshot ──► model agent + tools
       ▲                                           │
       │                                  validated decision ID
       └── settle read ◄── one mutation ◄── strict validator
```

## Decision boundary

Each decision is delivered as a compact line-based text brief: a short snapshot
fingerprint (the first 16 hex characters of the SHA-256 over the canonical public
JSON), delegated instructions and a durable plan, the public state rendered in
compressed form, and the enumerated legal actions. Hand cards have one-based
aliases (`c1`, `c2`); playable aliases/targets are grouped (`play: c1@2 c3`)
without repeating card names. Other actions keep short summaries (`a5 end_turn`). Repeated pile cards are aggregated
(`4x Defend`), pile order is not draw order, and keyword/mechanic definitions
are deduplicated into a single trailing glossary.
The uncompressed public JSON remains available through the read-only inspection
tools. An action has a small model-facing ID and
a controller-owned command. The model selects an ID and echoes the snapshot id;
the controller resolves it to the already-enumerated command only after checking
the snapshot fingerprint.

This keeps strategic freedom on the model side without giving generated text a
direct path to the game API.

The selected decision may contain up to 32 deterministic steps. Each `action_ids`
entry is an advertised action or card alias, optionally followed by known hand
choices: `["c3 c1", "a5"]` plays c3, chooses c1 for discard/exhaust, confirms,
then executes a5. Targeted plays use `cN@enemy`; existing a-IDs remain accepted.
References bind to the initial snapshot. Weakly held, process-local card instance
IDs distinguish identical copies across reordered hands and selection holders.
Inline selections use fresh legal actions, validate selection limits and chosen
cards, and confirm once. Only a completed selection with the expected remaining
hand, unchanged round and draw count can resume the original batch. Draws/random
effects, mismatches, other screen changes or unavailable actions return control
to the model. The step limit includes choices and confirmation. Explicit close,
confirm, proceed and end-turn actions remain terminal.

The controller handles routine UI transitions locally. It opens a merchant when
the entry control is available, closes an empty or exhausted inventory, confirms
a completed card or bundle selection preview, advances a mechanical dialogue
step, takes a single map path, and proceeds when no strategic choice remains.
The `auto_treasure` setting (on by default) makes treasure rooms fully
mechanical: the chest is opened, each revealed relic is claimed in order, and
the leaving proceed is the already-forced single remaining action; with the
setting off, treasure relics stay model-owned. A
map with an active Winged Boots charge stays model-controlled so the agent can
choose to spend that charge; once the counter is exhausted, the single-path
shortcut is available again.

Merchant entry is provider-independent, including the fake merchant event. Its
native binding searches initialized mutable events for the local player's matching
event; it does not call `LocalMutableEvent`, whose getter indexes a list that may
still be empty during room asset loading. An absent or stale event produces a
waiting snapshot with no shop controls, allowing the normal settle loop to wait
until the merchant can open. Purchases use the same initialized-event lookup.

The model initially receives a compact decision brief, then chooses which details
to retrieve with read-only function tools. It can inspect state sections,
list legal actions, query public enemy patterns, search discovered cards/relics/potions, and
review recent transitions. It finishes by calling the typed `take_action` terminal
tool. That call submits a decision to the controller; it does not mutate the game
itself. The controller resolves, freshness-checks, and executes the selected action
exactly once.

`lookup_enemy_moves` resolves the requested monster class from the installed
`sts2.dll` and uses the ILSpy library in process. It returns exact matches first,
falls back to up to five partial matches, and returns bounded decompiled source
(at most 24,000 characters per match) after a 15-second lookup deadline. Results
are cached in memory and on disk. The cache filename includes the assembly
SHA-256, so a new game build regenerates entries on demand.
When returned source calls `RandomBranchState.AddBranch`, the result also includes
conditional `decompilation_notes` describing every overload, parameter meaning,
and effective default so the model can distinguish cooldown from repeat limits.
This source lookup is static: it does not load game objects or read the current
intent, seed, RNG state, or a future rolled move. If no compatible decompiler is
available, the tool reports the lookup error and the model reasons under uncertainty.

Before packet construction, a recursive information firewall removes keys associated
with seeds, RNG state, draw/shuffle order, future rewards, future encounters, and
already-rolled future enemy moves. Human-available information remains allowed:
current intents, pile contents without order, the visible map and boss, and
documented enemy move probabilities.

## Sessions

The independent `use_jev_strategy` and `use_jev_combat` settings replace requests
to the respective gameplay model with Jev evaluations. The existing gameplay
holders still retain instructions and receive Buddy's updates, but no model
history or tool loop is opened for a Jev decision. Combat Solver is checked before
either combat provider, including combat-owned modal selections. A missing or
refusing solver falls through to the configured combat provider. Chat always uses
the normal Buddy session.

With Jev strategy enabled, reward gold, opening card rewards, relic claims, potion
claims into free slots, and proceeding after completion are local actions in that
order. Jev still chooses the card (or skip) on the card screen. Full-slot potion
rewards offer a replacement for each held slot plus a local skip. Skips are tracked
per run by weakly assigned reward object IDs, so claimed rows may reindex without
confusing identical potion rewards. Skipped entries are omitted from subsequent
Jev briefs, and the final proceed leaves them behind. Unrecognized reward types
remain model-controlled. Other gameplay providers keep their existing behavior.

Shop briefs and options filter sold items and prices above current gold before
building the keyword glossary. Full-slot purchases become replacement choices;
the ordinary purchase and standalone discard are withheld from Jev. Replacement
choices contain controller-owned discard and take steps. Only the real commands
reach the adapter, with its usual final freshness/cancellation checks for each.
After the discard settles, the controller verifies the remaining player state,
run, item identity and price, then re-resolves the claim/purchase against the fresh
legal actions. Uncertain outcomes stop play without a retry. A definitive change
ends the compound operation and returns to the normal fresh-decision loop.

Shop decisions use a dedicated question from `JevStrategy.Options`, starting with
the current gold balance. It compares purchases and removal as parts of a possible
sequence, alongside saving for a concrete need. Each purchase criterion includes
its remaining budget; removal and exiting have explicit descriptions. This replaces
the generic strategy question for shops, avoiding irrelevant reward instructions
and keeping one choice request per decision. Combat and other strategy screens
keep their own instructions. Action IDs and commands are unchanged.

Jev sends `state`, `model`, and typed `choice` questions to the exact configured
`jev_endpoint`, authenticated solely by `jev_api_key`. Every action ID maps to a
locally enumerated command. Groups contain at most 255 options, with a final
comparison when multiple groups are needed. Responses are validated against the
group's option set and converted into a decision bound locally to
the original snapshot. The common execution loop then performs freshness,
guidance, cancellation and action validation. Malformed answers never execute.

The Jev brief uses no history-dependent omissions. Unique held-card rules, public
enemy/orb/pet effects, upgrade previews and reachable visible map topology supplement
the existing compact renderer. Criteria carry the legal choices once; the brief
omits its action list. Only four recent action summaries and the last definitive
failure feedback accompany current state. The existing context setting bounds
the UTF-8 token estimate of the entire request. HTTP 429/529 evaluations retry at
most twice with backoff (or bounded Retry-After); game mutations are never retried.
`jev_response` traces contain selected IDs and usage, without credentials or full
payloads. Jev and Buddy credentials are independently persisted, masked, and
omitted from status responses.

`BotRuntime` has three agent roles with independently bounded sessions:

1. Buddy is the persistent main chat agent. A serial queue preserves submission
   order and replays complete assistant/tool/reasoning transcripts between messages.
2. Buddy starts at most one game subagent. It handles menus, paths, events, shops,
   rewards and run strategy. A run assignment normally ends at game over, but can
   also end because the player stopped it, an error occurred, or the step limit
   was reached. A fight assignment requires active combat and ends before rewards
   or navigation.
3. On entering `monster`, `elite`, `boss`, or `hand_select`, the game controller
   launches at most one combat subagent with the game's instructions and plan.
   The game agent waits while combat owns decisions. Leaving combat returns a
   tactical report to the game agent; the next combat gets a fresh session.

A single gameplay worker owns action execution, so game and combat agents cannot
mutate concurrently. Buddy can continue chatting while that worker reasons or acts.
Its `start_game`, `message_agent`, `stop_game`, and `inspect_agents` tools coordinate
subagents. Game-directed guidance also reaches an active combat agent. New guidance
invalidates an in-flight decision before another action can execute. User chat is
not automatically copied into gameplay; Buddy relays relevant instructions. The
game agent can also change between run and fight scope when Buddy relays a new
request. Successful control operations remain idempotent across context retries.

`AgentSession` owns history, cache identity, state-diff memory and token accounting.
Responses uses `store = false` and replays encrypted reasoning, calls and results;
Chat Completions replays equivalent assistant/tool messages. Each request checks
both a conservative UTF-8 estimate (including tool schemas) and reported token usage.
On overflow Buddy starts a new session with current public state and previous chats.
If that does not fit, it keeps only state and the latest user message; finally it
tries state alone. Game and combat sessions restart with current public state,
legal actions, delegated instructions and their plan. All retries are bounded;
state that cannot fit produces an actionable error. Provider context-limit errors
use the same fallback. Restarting clears whole tool protocols and definition memory,
never leaving orphaned tool results. The visible chat feed survives rollovers,
settings changes, stopping and game-state changes.

After the controller executes `take_action`, its result is returned to that same
conversation on the next decision. The result carries per-action outcomes (command,
short summary, settled flag, and the post-action snapshot fingerprint) plus an
advisory `legal_actions` hint (IDs and summaries only) for the new snapshot, so the
model can decide without another `list_legal_actions` round trip. Full post-action
state is not embedded in outcomes; the next decision turn renders a fresh text
brief. The controller waits for a settled read before each decision and
revalidates the authoritative legal actions immediately before every mutation.
Empty or discard-only snapshots are still transitioning: the controller keeps
polling until a non-discard action is available (or the run ends), within its
30-second settle timeout. This lets delayed proceed buttons enter the action
result and the existing automatic navigation path without a model refresh call.
Ancient-event snapshots include the current dialogue line's position: the body,
options and enabled hitbox can remain identical across consecutive lines. This
lets each dialogue advance settle on observed progress before the next automatic
click, without relying on animation frames or reading upcoming dialogue.
Event options also report their text key: multi-line events such as The Architect
regenerate an identical-looking single option for every dialogue line (same title,
description and flags, even for the final run-winning choice), and the key carries
the line position so each consumed click settles the same way.
A settle wait that ends with the screen still interactive and byte-identical to
the pre-click snapshot is a silent no-op click - the game accepted the input but
refused its effect, e.g. a potion reward with full potion slots, whose button
stays enabled. The mutation itself is still never retried; the action comes back
as a rejected submission (`executed=false`, unchanged snapshot) and the model
re-decides. Claiming a potion reward with full slots is rejected up front with
the same recovery. Three consecutive ineffective clicks on one snapshot stop
play with an explicit report instead of looping.
A queued operation that is cancelled, times out, or sees a changed snapshot is
rejected without retrying the mutation.

## Prompt caching

Stable agent instructions and tool schemas start each session. Both API modes
replay their complete history so providers can reuse the prefix. Requests keep the
same `prompt_cache_key` until that agent's session rolls over. Responses requests
for GPT-5.6 also specify the existing 30-minute cache TTL. Usage is recorded in
model-response traces.

Ordinary settings saves preserve Buddy's history and cache identity. Effective
personality changes append a user instruction before the next chat message;
unchanged settings and inactive custom descriptions add no note. New sessions
and context rollovers bake the current personality into the initial prompt.
Endpoint, model, or API-format changes rebuild Buddy from completed chats so
provider-specific tool and reasoning items do not cross those boundaries.

## Operator chat

The panel has one empty composer and shows example prompts in the initial chat
window, including `Win with ironclad`, `win this fight`, `continue this run`,
`What should I do next?`, `What are my potion reward chances?`,
`What moves can this enemy use?`, and `What should I pick from these rewards?`.
It also suggests asking which card, relic, path, or shop option is best. There is
no goal setting or separate Start control; the player uses natural-language
messages in the composer and Buddy decides whether to answer, steer active play,
or start play.

Buddy reads fresh public state before each user turn. The first turn includes the
state and play status. Identical observations add nothing. Small changes use
JSON Pointer `set`/`remove` operations, including deletions; if a replacement is
smaller, it sends the full state. Unchanged deck or relic data is not repeatedly
prefixed to follow-up questions. State is filtered through the same information
firewall used by gameplay. Tool rounds refresh the diff baseline as well, so a
change back to an earlier value is reported correctly after an inspection.

When play ends between turns, the final report is queued as a `Play update` note
and delivered in conversation order on the next user message, and any
play-status change (start, stop, run end) re-anchors the session by resending
the full state even when the raw snapshot is unchanged. A request that arrives
right after a finished run therefore always sees both the end-of-run brief and
the current menu picture, never a bare message over stale run discussion.

A literal `stop` is handled immediately without a model request, cancelling gameplay
reasoning and queued actions while retaining Buddy's conversation. A control epoch
prevents older queued or in-flight chat responses from restarting play afterward.
Other stop phrasing can use Buddy's `stop_game` tool. Every action still checks stop
and freshness on the game thread; an already executed action is never retried.

Buddy keeps read-only tools for public state, enemy rules, discovered cards/relics/potions,
recent actions, run history statistics and game odds. It has no `take_action` tool.
Run-history statistics and odds use `ReadKnowledge`, cached for 30 seconds. Replies
and relayed gameplay commentary render as Markdown in the panel.

## Loop invariants

1. Read a fresh state.
2. Enumerate legal actions from that exact state.
3. Build a bounded packet.
4. Request one decision through the typed `take_action` terminal tool and validate it.
5. Resolve the selected action ID from the current candidate table.
6. Send the mutation exactly once on the game thread. A definitive rejection is
   returned to the same agent session so it can choose again; uncertain failures
   still stop the controller to prevent duplicate mutations.
7. Poll until the public state fingerprint changes.
8. Write runtime events, model usage, stale-decision records, and per-action
   outcomes to JSONL; full before-and-after public snapshots are not stored.

If a provider mistakenly emits prose instead of calling `take_action`, the client
makes one repair request that forces only that tool. An invalid model answer can then
be corrected with validation feedback. A game mutation cannot be retried because a
transport failure may happen after the game accepted it.

## Extension points

`IGameAdapter` is the boundary between the worker and the game thread:

```csharp
interface IGameAdapter
{
    Task<JsonNode> ReadState(CancellationToken ct);
    Task<JsonNode> Execute(JsonNode command, string expectedSnapshot,
        Func<bool> mayExecute, CancellationToken ct);
    Task<JsonNode> Search(string query, string itemType, CancellationToken ct);
    Task<JsonNode> ReadKnowledge(CancellationToken ct);
}
```

The concrete adapter schedules every read and mutation onto Godot's main-thread
queue. It takes a fresh public snapshot immediately before a mutation, compares its
fingerprint with the model's snapshot, checks that the command is still enumerated,
and then invokes the game binding exactly once. A queued operation that is cancelled
or times out is skipped when a later frame drains the queue.
