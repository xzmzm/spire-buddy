#!/usr/bin/env python3
"""Build and install the native panel. Restart the game to load updates."""

import argparse
import datetime
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


def install_file(source: Path, target: Path) -> None:
    """Replace the directory entry, never overwrite a DLL mapped by the game."""
    # Stage on the same filesystem so rename is atomic. Keep staging files on
    # failure for diagnosis; never truncate the currently installed assembly.
    staging = Path(tempfile.mkdtemp(prefix=".spire-buddy-install-", dir=target.parent))
    staged = staging / target.name
    shutil.copy2(source, staged)
    if target.exists():
        backup = target.with_name(
            target.name + "." + datetime.datetime.now().strftime("%Y%m%d%H%M%S%f") + ".backup"
        )
        # Preserve the actual old inode, including any mappings held by the game.
        os.link(target, backup)
    os.replace(staged, target)


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--game",
        type=Path,
        default=Path.home() / "Library/Application Support/Steam/steamapps/common/Slay the Spire 2",
    )
    args = parser.parse_args()
    bundle = args.game / "SlayTheSpire2.app/Contents"
    if bundle.exists():
        data = bundle / "Resources/data_sts2_macos_arm64"
        mods = bundle / "MacOS/mods"
    else:
        data = next((p for p in args.game.glob("data_sts2_*") if (p / "sts2.dll").exists()), None)
        mods = args.game / "mods"
    if data is None or not (data / "sts2.dll").exists():
        parser.error("Could not find the game assemblies; pass --game with the install directory")
    project = root / "mod/SpireBuddy"
    subprocess.run(
        ["dotnet", "build", str(project), "-c", "Release", f"-p:STS2GameDataDir={data}"], check=True
    )
    mods.mkdir(parents=True, exist_ok=True)
    sources = [
        project / "bin/Release/net9.0/SpireBuddy.dll",
        project / "bin/Release/net9.0/ICSharpCode.Decompiler.dll",
        project / "SpireBuddy.json",
        project / "SpireBuddy.third-party-notices.txt",
    ]
    for source in sources:
        install_file(source, mods / source.name)
    legacy_files = [mods / name for name in (
        "STS2Bot.dll", "STS2Bot.json", "STS2Bot.third-party-notices.txt"
    ) if (mods / name).exists()]
    if legacy_files:
        backup = Path(tempfile.mkdtemp(prefix=".spire-buddy-legacy-", dir=mods))
        for legacy in legacy_files:
            legacy.rename(backup / (legacy.name + ".previous"))
    print(f"Installed Spire Buddy in {mods}. Restart the game when ready; enable the mod.")


if __name__ == "__main__":
    main()
