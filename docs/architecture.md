# Native runtime

`mod/SpireBuddy/Runtime` owns the controller and talks to the model endpoint. The
Godot panel dispatches in-process calls; it does not use an HTTP control server.
`mod/SpireBuddy/Game` reads the live game objects, filters observations, enumerates
commands, and dispatches clicks or game commands on the main thread.
`GameState` filters observations and enumerates commands, `ActionBatch`
validates/re-resolves batches, `ModelClient` handles the two API formats, and
`EnemyDecompiler` reads static assembly source through ILSpy's DLL.
`BotRuntime` owns lifecycle, settings, session history, operator chat and traces.
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
step, takes a single map path, and proceeds when no strategic choice remains. A
map with an active Winged Boots charge stays model-controlled so the agent can
choose to spend that charge; once the counter is exhausted, the single-path
shortcut is available again.

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
