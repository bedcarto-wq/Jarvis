using Jarvis.Core.Abstractions;
using Jarvis.Core.Text;

namespace Jarvis.Core.Apps;

public sealed record AppMatch(AppEntry App, double Score, string MatchedName);

public sealed record AppMatchResult(AppMatch? Best, IReadOnlyList<AppMatch> Candidates, bool IsAmbiguous)
{
    public bool Found => Best is not null && !IsAmbiguous;
    public static readonly AppMatchResult None = new(null, [], false);
}

/// <summary>Сопоставление произнесённого названия с программой.</summary>
public static class AppMatcher
{
    public const double Threshold = 0.72;
    private const double AmbiguityGap = 0.04;
    private static readonly string[] Fillers =
        ["браузер", "программу", "программа", "приложение", "игру", "игра", "окно", "мне", "пожалуйста", "app", "the"];

    public static AppMatchResult Match(string spoken, IEnumerable<AppEntry> apps)
    {
        var target = StripFillers(TextNormalizer.Normalize(spoken));
        if (target.Length == 0) return AppMatchResult.None;
        var targetKey = Transliterator.ToLatinKey(target);

        var scored = new List<AppMatch>();
        foreach (var app in apps)
        {
            AppMatch? best = null;
            foreach (var name in app.AllNames())
            {
                var n = TextNormalizer.Normalize(name);
                if (n.Length == 0) continue;
                var s = Score(target, targetKey, n);
                if (best is null || s > best.Score) best = new AppMatch(app, s, name);
            }
            if (best is not null && best.Score >= Threshold) scored.Add(best);
        }
        if (scored.Count == 0) return AppMatchResult.None;
        var ordered = scored.OrderByDescending(m => m.Score).ToList();
        var top = ordered[0];
        bool ambiguous = ordered.Count > 1 && top.Score < 1.0 && top.Score - ordered[1].Score < AmbiguityGap;
        if (ambiguous)
        {
            var close = ordered.TakeWhile(m => top.Score - m.Score < AmbiguityGap).Take(4).ToList();
            return new AppMatchResult(top, close, true);
        }
        return new AppMatchResult(top, ordered.Take(4).ToList(), false);
    }

    private static string StripFillers(string t)
    {
        var words = TextNormalizer.Words(t).ToList();
        if (words.Count <= 1) return t;
        var kept = words.Where(w => !Fillers.Contains(w)).ToList();
        return kept.Count == 0 ? t : string.Join(' ', kept);
    }

    internal static double Score(string target, string targetKey, string name)
    {
        if (target == name) return 1.0;
        var nameKey = Transliterator.ToLatinKey(name);
        if (targetKey == nameKey) return 0.96;
        double s = 0;
        // Совпадение целым словом: «телеграм десктоп» ~ «телеграм».
        if (TextNormalizer.ContainsWords(name, target) || TextNormalizer.ContainsWords(target, name))
        {
            double ratio = Math.Min(target.Length, name.Length) / (double)Math.Max(target.Length, name.Length);
            s = Math.Max(s, 0.80 + 0.12 * ratio);
        }
        if (nameKey.Length > 0 && targetKey.Length > 0)
        {
            s = Math.Max(s, 0.92 * Fuzzy.Similarity(targetKey, nameKey));
            // Сравнение с первым словом длинного названия («Discord PTB»).
            var first = nameKey.Split(' ')[0];
            if (first.Length >= 4) s = Math.Max(s, 0.88 * Fuzzy.Similarity(targetKey, first));
        }
        return s;
    }
}

/// <summary>Поиск окон, принадлежащих программе.</summary>
public static class WindowMatcher
{
    public static IReadOnlyList<WindowInfo> FindWindows(IEnumerable<WindowInfo> windows, AppEntry app)
    {
        var procs = new HashSet<string>(app.ProcessNames.Select(p => p.ToLowerInvariant()));
        var list = windows.Where(w => procs.Contains(w.ProcessName.ToLowerInvariant())).ToList();
        if (list.Count == 0)
        {
            var names = app.AllNames().Select(TextNormalizer.Normalize).Where(n => n.Length >= 3).ToList();
            list = windows.Where(w =>
            {
                var t = TextNormalizer.Normalize(w.Title);
                return names.Any(n => TextNormalizer.ContainsWords(t, n));
            }).ToList();
        }
        // Активное окно первым, затем не свёрнутые.
        return list.OrderByDescending(w => w.IsForeground).ThenBy(w => w.IsMinimized).ToList();
    }

    public static IReadOnlyList<WindowInfo> FindByTitle(IEnumerable<WindowInfo> windows, string titlePart)
    {
        var n = TextNormalizer.Normalize(titlePart);
        return windows.Where(w => TextNormalizer.Normalize(w.Title).Contains(n, StringComparison.Ordinal)).ToList();
    }
}
