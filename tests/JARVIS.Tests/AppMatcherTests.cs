using Jarvis.Core.Apps;
using Jarvis.Core.Text;

namespace Jarvis.Tests;

public class AppMatcherTests
{
    [Fact]
    public void DiscoveredLatinApp_MatchedByCyrillicPronunciation()
    {
        var catalog = new AppCatalog();
        catalog.Merge([new AppEntry { DisplayName = "Notion", LaunchTarget = @"C:\Apps\Notion.exe", ProcessNames = ["notion"] }]);
        var m = AppMatcher.Match("нотион", catalog.All);
        Assert.True(m.Found);
        Assert.Equal("notion", m.Best!.App.Id);
    }

    [Fact]
    public void Merge_AttachesBuiltInAliasesAndKeepsUserEdits()
    {
        var catalog = new AppCatalog();
        catalog.Merge([new AppEntry { DisplayName = "Google Chrome", LaunchTarget = @"C:\Chrome\chrome.exe", ProcessNames = ["chrome"], Source = AppSource.StartMenu }]);
        var chrome = catalog.Get("chrome")!;
        Assert.Equal(@"C:\Chrome\chrome.exe", chrome.LaunchTarget);
        Assert.Contains("хром", chrome.Aliases);

        chrome.UserEdited = true;
        chrome.LaunchTarget = @"D:\custom\chrome.exe";
        catalog.Merge([new AppEntry { DisplayName = "Google Chrome", LaunchTarget = @"C:\Other\chrome.exe", ProcessNames = ["chrome"] }]);
        Assert.Equal(@"D:\custom\chrome.exe", catalog.Get("chrome")!.LaunchTarget);
    }

    [Fact]
    public void Catalog_PersistsUserAliases()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "apps.json");
        var a = new AppCatalog(path);
        a.Upsert(new AppEntry { Id = "obsidian", DisplayName = "Obsidian", Aliases = ["обсидиан"], UserEdited = true });
        var b = new AppCatalog(path);
        b.Load();
        Assert.Contains("обсидиан", b.Get("obsidian")!.Aliases);
        Assert.NotNull(b.Get("chrome"));
    }

    [Theory]
    [InlineData("chrome", "хром")]
    [InlineData("telegram", "телеграм")]
    [InlineData("discord", "дискорд")]
    public void Transliteration_KeysMatch(string latin, string cyr) =>
        Assert.Equal(Transliterator.ToLatinKey(latin), Transliterator.ToLatinKey(cyr));

    [Fact]
    public void ToCyrillic_ProducesPronounceableAlias()
    {
        Assert.Equal("хром", Transliterator.ToCyrillic("chrome"));
        Assert.Equal("телеграм", Transliterator.ToCyrillic("telegram"));
    }
}
