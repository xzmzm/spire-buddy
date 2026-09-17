// Statistics and odds read from the game's own per-profile save data: run
// history records the game already persists, and the odds values the game
// itself tracks for the live run. No hidden RNG state is exposed beyond those
// public counters.
using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireBuddy.Game;

internal static partial class GameBindings
{
    private const int MaxReportedRuns = 60;

    internal static Dictionary<string, object?> ReadKnowledge()
    {
        return new Dictionary<string, object?>
        {
            ["odds"] = Safe(BuildOdds),
            ["run_history"] = Safe(BuildRunHistory),
        };
    }

    private static Dictionary<string, object?> Safe(Func<Dictionary<string, object?>> build)
    {
        try { return build(); }
        catch (Exception) { return Error("This knowledge is unavailable right now."); }
    }

    private static Dictionary<string, object?> BuildOdds()
    {
        var runState = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        var player = runState != null && runState.Players.Count == 1 ? LocalContext.GetMe(runState) : null;
        var potion = player?.PlayerOdds?.PotionReward;
        if (potion == null) return Error("No singleplayer run in progress.");
        var normal = Math.Min(100m, Math.Round((decimal)potion.CurrentValue * 100m, 1));
        var elite = Math.Min(100m, Math.Round(((decimal)potion.CurrentValue + 0.125m) * 100m, 1));
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["potion_reward_chance_normal_combat"] = normal.ToString("0.##") + "%",
            ["potion_reward_chance_elite_combat"] = elite.ToString("0.##") + "%",
            ["rules"] = "Potion rewards start at 40%. Each combat without a potion drop raises the next chance by 10 points; each drop lowers it by 10 points, so roughly half of normal combats award potions overall. Elite combats roll with a 12.5 point bonus.",
        };
    }

    private static Dictionary<string, object?> BuildRunHistory()
    {
        var save = SaveManager.Instance;
        if (save == null || !save.IsProfileInitialized) return Error("No profile data available.");
        var names = save.GetAllRunHistoryNames() ?? new List<string>();
        // History files are named "<unix start time>.run"; newest first so
        // streaks count from the most recent run.
        var ordered = names
            .Select(name => (Name: name, Start: ParseHistoryStart(name)))
            .OrderByDescending(entry => entry.Start)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Take(MaxReportedRuns)
            .ToList();
        var summaries = new List<Dictionary<string, object?>>();
        var wins = new List<bool>();
        foreach (var (name, _) in ordered)
        {
            RunHistory run;
            try
            {
                var read = save.LoadRunHistory(name);
                if (!read.Success || read.SaveData == null) continue;
                run = read.SaveData;
            }
            catch (Exception) { continue; }
            wins.Add(run.Win);
            summaries.Add(new Dictionary<string, object?>
            {
                ["started"] = DateTimeOffset.FromUnixTimeSeconds(run.StartTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC",
                ["win"] = run.Win,
                ["abandoned"] = run.WasAbandoned,
                ["character"] = string.Join(" & ", run.Players.Select(p => CharacterLabel(p.Character))),
                ["ascension"] = run.Ascension,
                ["floors_reached"] = run.MapPointHistory?.Sum(points => points.Count) ?? 0,
                ["acts"] = run.Acts?.Count ?? 0,
                ["game_mode"] = run.GameMode.ToString(),
                ["duration_minutes"] = Math.Round(run.RunTime / 60f, 1),
                ["deck_size"] = run.Players.FirstOrDefault()?.Deck?.Count() ?? 0,
                ["killed_by_encounter"] = IdOrEmpty(run.KilledByEncounter),
                ["killed_by_event"] = IdOrEmpty(run.KilledByEvent),
            });
        }
        if (summaries.Count == 0)
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["total_saved_runs"] = names.Count,
                ["message"] = "No loadable run history records for this profile yet.",
            };
        var currentStreak = 0;
        while (currentStreak < wins.Count && wins[currentStreak]) currentStreak++;
        var bestStreak = 0;
        for (int i = 0, run_ = 0; i < wins.Count; i++)
        {
            run_ = wins[i] ? run_ + 1 : 0;
            bestStreak = Math.Max(bestStreak, run_);
        }
        var won = wins.Count(w => w);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["scope"] = $"Newest {summaries.Count} of {names.Count} saved runs for this profile.",
            ["total_saved_runs"] = names.Count,
            ["reported_runs"] = summaries.Count,
            ["wins"] = won,
            ["losses"] = summaries.Count - won,
            ["win_rate"] = Math.Round(100m * won / summaries.Count, 1).ToString("0.##") + "%",
            ["current_win_streak"] = currentStreak,
            ["best_win_streak"] = bestStreak,
            ["runs"] = summaries,
        };
    }

    private static long ParseHistoryStart(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return long.TryParse(name, out var start) ? start : 0;
    }

    private static string CharacterLabel(MegaCrit.Sts2.Core.Models.ModelId character)
    {
        var id = IdOrEmpty(character);
        return id.Length == 0 ? "unknown" : id;
    }

    private static string IdOrEmpty(MegaCrit.Sts2.Core.Models.ModelId id) => SafeGetText(() => id.Entry) ?? "";
}
