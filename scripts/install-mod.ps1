# Build and install the native panel. Restart the game to load updates.
#
# Windows counterpart of scripts/install-mod.command. Pass -Game with the game
# installation directory, or omit it to search the Steam libraries registered
# on this machine (Steam app 2868840). The Python installer in this folder
# remains available as a cross-platform alternative.

[CmdletBinding()]
param(
    # Game installation directory containing a data_sts2_* folder.
    [Parameter(Position = 0)] [string] $Game
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.Platform -and $PSVersionTable.Platform -ne 'Win32NT') {
    throw 'This installer is Windows-only; use scripts/install-mod.command instead.'
}

if (-not ('SpireBuddy.Win32Files' -as [type])) {
    Add-Type -Namespace SpireBuddy -Name Win32Files -MemberDefinition @'
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
'@
}
$MoveFileReplaceExisting = 1

function Get-SteamLibrary {
    # Steam roots known to this machine: the registry SteamPath of each Steam
    # install plus every library listed in its libraryfolders.vdf.
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $steam = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).SteamPath
        if (-not $steam) { continue }
        $steam = [IO.Path]::GetFullPath($steam)
        if ($roots -notcontains $steam) { $roots += $steam }
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        foreach ($line in Get-Content -LiteralPath $vdf -ErrorAction SilentlyContinue) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $library = [IO.Path]::GetFullPath(($Matches[1] -replace '\\\\', '\'))
                if ($roots -notcontains $library) { $roots += $library }
            }
        }
    }
    $roots
}

function Find-Sts2Data {
    param([Parameter(Mandatory)] [string] $GameDir)
    Get-ChildItem -LiteralPath $GameDir -Directory -Filter 'data_sts2_*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'sts2.dll') } |
        Select-Object -ExpandProperty FullName -First 1
}

function Find-Sts2Game {
    # Candidate install folders: the installdir recorded for Slay the Spire 2
    # in each library's app manifests, plus the default folder name.
    $candidates = @()
    foreach ($root in Get-SteamLibrary) {
        $apps = Join-Path $root 'steamapps'
        foreach ($manifest in Get-ChildItem -LiteralPath $apps -Filter 'appmanifest_*.acf' -File -ErrorAction SilentlyContinue) {
            $text = Get-Content -LiteralPath $manifest.FullName -Raw
            if ($text -match '"appid"\s+"2868840"' -or $text -match '"name"\s+"Slay the Spire 2"') {
                if ($text -match '"installdir"\s+"([^"]+)"') {
                    $candidates += (Join-Path $apps ('common\' + $Matches[1]))
                }
            }
        }
        $candidates += (Join-Path $apps 'common\Slay the Spire 2')
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Find-Sts2Data -GameDir $candidate) { return $candidate }
    }
}

function Install-File {
    # Replace the directory entry, never the file contents, so an assembly
    # mapped by the running game keeps its inode. The staged file is left
    # behind on failure for diagnosis; the hard link preserves the old inode
    # as a recoverable backup.
    param([Parameter(Mandatory)] [string] $Source, [Parameter(Mandatory)] [string] $Target)
    $directory = Split-Path -Parent $Target
    $fileName = Split-Path -Leaf $Target
    $staged = Join-Path $directory ('.spire-buddy-install-' + [IO.Path]::GetRandomFileName() + '.staged')
    Copy-Item -LiteralPath $Source -Destination $staged
    if (Test-Path -LiteralPath $Target) {
        $backup = Join-Path $directory ($fileName + '.' + (Get-Date -Format 'yyyyMMddHHmmssffffff') + '.backup')
        New-Item -ItemType HardLink -Path $backup -Target $Target | Out-Null
    }
    $moved = [SpireBuddy.Win32Files]::MoveFileEx($staged, $Target, $MoveFileReplaceExisting)
    if (-not $moved) {
        throw ("Replacing '{0}' failed with Win32 error {1}." -f $Target,
            [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
}

function Remove-EmptyStagingDirs {
    # Older installers left empty staging directories behind; recycle them
    # once they are provably empty so diagnosis files are never destroyed.
    param([Parameter(Mandatory)] [string] $Mods)
    Add-Type -AssemblyName Microsoft.VisualBasic
    foreach ($dir in Get-ChildItem -LiteralPath $Mods -Directory -ErrorAction SilentlyContinue) {
        if ($dir.Name -like '.spire-buddy-install*' -and -not (Get-ChildItem -LiteralPath $dir.FullName -Force)) {
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($dir.FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
        }
    }
}

$root = Split-Path -Parent $PSScriptRoot

if (-not $Game) {
    Write-Host 'No game directory given; searching Steam libraries for Slay the Spire 2...'
    $Game = Find-Sts2Game
}
if (-not $Game -or -not (Test-Path -LiteralPath $Game -PathType Container)) {
    throw ('Could not find the game installation. Pass -Game with the install ' +
        'directory (the folder containing data_sts2_*, e.g. "D:\SteamLibrary\steamapps\common\Slay the Spire 2").')
}
$Game = [IO.Path]::GetFullPath($Game)
$data = Find-Sts2Data -GameDir $Game
if (-not $data -or -not (Test-Path -LiteralPath (Join-Path $data 'GodotSharp.dll'))) {
    throw "Could not find the game assemblies (sts2.dll/GodotSharp.dll) under '$Game'."
}
$mods = Join-Path $Game 'mods'
$project = Join-Path $root 'mod\SpireBuddy'

Write-Host "Game: $Game"
& dotnet build $project -c Release "-p:STS2GameDataDir=$data"
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

New-Item -ItemType Directory -Path $mods -Force | Out-Null
$sources = @(
    (Join-Path $project 'bin\Release\net9.0\SpireBuddy.dll'),
    (Join-Path $project 'bin\Release\net9.0\ICSharpCode.Decompiler.dll'),
    (Join-Path $project 'SpireBuddy.json'),
    (Join-Path $project 'SpireBuddy.third-party-notices.txt')
)
foreach ($source in $sources) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing build output: $source" }
    Install-File -Source $source -Target (Join-Path $mods (Split-Path -Leaf $source))
}

# Retire the previous identity so the game cannot load both mods.
$legacy = @('STS2Bot.dll', 'STS2Bot.json', 'STS2Bot.third-party-notices.txt') |
    ForEach-Object { Join-Path $mods $_ } |
    Where-Object { Test-Path -LiteralPath $_ }
if ($legacy) {
    $retired = Join-Path $mods ('.spire-buddy-legacy-' + [IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $retired | Out-Null
    foreach ($file in $legacy) {
        [IO.File]::Move($file, (Join-Path $retired ((Split-Path -Leaf $file) + '.previous')))
    }
}

Remove-EmptyStagingDirs -Mods $mods
Write-Host "Installed Spire Buddy in $mods. Restart the game when ready; enable the mod."
