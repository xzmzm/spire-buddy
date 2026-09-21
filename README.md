# Spire Buddy for Slay the Spire 2

Spire Buddy is a self-contained C# game mod with an in-game chat and settings
panel. It reads the live game through an in-process adapter, sends requests
directly to a configured OpenAI-compatible endpoint, validates every game action,
and performs enemy decompilation in process. No companion service or browser
dashboard is required.

Optional [Jev decisions](mod/README.md#jev-decisions) use TypeSafe's choice API for
run strategy, combat, or both, with a separate endpoint, model and API key.
Settings are grouped into sections that start collapsed.

## Install

Prerequisites are a working Slay the Spire 2 mod-loader installation and the
.NET 9 SDK. The installer builds against the game's `sts2.dll` and
`GodotSharp.dll`, then copies the mod assembly, ILSpy decompiler dependency,
manifest, and third-party notices to the game's `mods` directory.

On macOS, using the bundled arm64 game-data layout:

```sh
./scripts/install-mod.command
./scripts/install-mod.command "/path/to/Slay the Spire 2"
```

On Linux, pass the game installation directory explicitly:

```sh
./scripts/install-mod.command "/path/to/Slay the Spire 2"
```

On Windows, use the PowerShell installer. With no game directory it searches
the Steam libraries registered on the machine:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-mod.ps1
powershell -ExecutionPolicy Bypass -File scripts\install-mod.ps1 -Game "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

Restart the game after installation, enable **Spire Buddy**, and configure the
model in Settings. The installer leaves recoverable backups when replacing
installed files and does not touch the mod's saved settings, panel layout, cache,
or run traces.

## Use Buddy

The initial chat window includes examples for starting a run, winning the current
fight, continuing a run, asking what to do next, checking potion odds, looking up
enemy moves, choosing rewards, and comparing card, relic, path, or shop options.
Questions do not start play by themselves. Say **continue this run** or
**win this fight** to control play, and say **stop** to cancel play while keeping
Buddy available for conversation.

Buddy can answer questions about the current state, discovered cards, relics and potions,
static enemy rules, recent actions, saved run statistics, and potion reward odds.
It keeps one player-facing voice, preserves the chat conversation during the game
process, and sends only changed public state on follow-up turns. Replies and
gameplay commentary render as Markdown in the panel. The panel UI follows the
game's language — Chinese when the game runs in Chinese, English otherwise —
and **stop** (or **停**) cancels play.

## Stop an OBS recording with Buddy (optional)

The [OBS watcher](scripts/stop-obs-with-buddy.py) stops an **already running**
recording when Buddy is stopped by the player, finishes a fight/run, or exits
with an API/gameplay error. It also stops after a hard time limit, covering an
API request that never returns. It does not start OBS or recording. If the game
crashes without writing an end marker, the time limit still applies.

In OBS, enable **Tools → obs-websocket Settings → Enable WebSocket server** and
leave authentication on. The script reads the local OBS configuration for the
port and password; it never prints or copies the password. Install the Python
client in a virtual environment, then start recording in OBS and run (macOS/Linux):

```sh
python3 -m venv .obs-venv
.obs-venv/bin/python -m pip install websocket-client
.obs-venv/bin/python scripts/stop-obs-with-buddy.py --max-minutes 120
```

Run the watcher before or during Buddy's play. It exits after stopping the
recording; run it again for a new recording. `Ctrl-C` cancels only the watcher,
not OBS. For a nonstandard Godot data directory, pass `--traces PATH`; for a
nonstandard OBS configuration, pass `--obs-config PATH`. Existing recordings
remain under OBS's normal output settings. The watcher is opt-in so unrelated
OBS recordings are never stopped automatically.

See [the mod guide](mod/README.md) for configuration, controls, safety,
installation paths, and verification. The implementation overview is in
[docs/architecture.md](docs/architecture.md).

## Build and test

The mod project needs the path to a game data directory containing `sts2.dll`
and `GodotSharp.dll`:

```sh
dotnet build mod/SpireBuddy -c Release -p:STS2GameDataDir="/path/to/game/data"
dotnet build mod/SpireBuddy.Tests -c Release
dotnet run --project mod/SpireBuddy.Tests -c Release
```

The tests target `net9.0`; install the .NET 9 runtime to run them. The test
project does not need Godot or the game assemblies unless you also run its
optional real-decompilation check.
