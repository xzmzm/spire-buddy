#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
game="${1:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
if [[ -d "$game/SlayTheSpire2.app" ]]; then
  data="$game/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
  mods="$game/SlayTheSpire2.app/Contents/MacOS/mods"
else
  data=""
  for candidate in "$game"/data_sts2_*; do
    if [[ -f "$candidate/sts2.dll" ]]; then data="$candidate"; break; fi
  done
  mods="$game/mods"
fi
[[ -f "$data/sts2.dll" ]] || { echo "Game assemblies not found: $game" >&2; exit 1; }
dotnet build "$root/mod/SpireBuddy" -c Release "-p:STS2GameDataDir=$data"
mkdir -p "$mods"
stage="$(mktemp -d "$mods/.spire-buddy-install.XXXXXX")"
for source in "$root/mod/SpireBuddy/bin/Release/net9.0/SpireBuddy.dll" "$root/mod/SpireBuddy/bin/Release/net9.0/ICSharpCode.Decompiler.dll" "$root/mod/SpireBuddy/SpireBuddy.json" "$root/mod/SpireBuddy/SpireBuddy.third-party-notices.txt"; do
  name="$(basename "$source")"
  cp -p "$source" "$stage/$name"
  if [[ -e "$mods/$name" ]]; then ln "$mods/$name" "$stage/$name.previous"; fi
  mv -f "$stage/$name" "$mods/$name"
done
# Retire the previous identity so the game cannot load both mods.
for name in STS2Bot.dll STS2Bot.json STS2Bot.third-party-notices.txt; do
  if [[ -e "$mods/$name" ]]; then mv "$mods/$name" "$stage/$name.previous"; fi
done
printf 'Installed Spire Buddy in %s. Restart the game to load it.\n' "$mods"
