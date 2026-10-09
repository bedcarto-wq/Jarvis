using Jarvis.Core.Apps;
using Jarvis.Core.Commands;
using Jarvis.Core.Settings;

namespace Jarvis.Voice.Recognition;

/// <summary>Собирает грамматику из настроек, каталога приложений и голосовых фраз шаблонов.</summary>
public static class VocabularyBuilder
{
    public static GrammarSpec Build(JarvisSettings settings, IEnumerable<AppEntry> apps, IEnumerable<string> templatePhrases,
        IEnumerable<string>? extraNames = null)
    {
        var appNames = apps.Where(a => !a.Hidden).SelectMany(a => a.AllNames())
            .Select(GrammarSpec.Clean).Where(n => n.Length > 0 && n.Split(' ').Length <= 4).Distinct().ToList();
        var sites = CommandLexicon.Sites.Keys.Select(GrammarSpec.Clean).Where(s => s.Length > 0).Distinct().ToList();
        return new GrammarSpec
        {
            WakePhrases = settings.WakePhrases.Select(GrammarSpec.Clean).Where(p => p.Length > 0).Distinct().ToList(),
            StopWord = GrammarSpec.Clean(settings.StopWord),
            AppNames = appNames,
            SiteNames = sites,
            TemplatePhrases = templatePhrases.Select(GrammarSpec.Clean).Where(p => p.Length > 0).Distinct().ToList(),
            Names = GrammarSpec.DefaultNames.Concat(extraNames ?? []).Select(GrammarSpec.Clean).Where(p => p.Length > 0).Distinct().ToList(),
            IncludeGarbage = true,
        };
    }
}
