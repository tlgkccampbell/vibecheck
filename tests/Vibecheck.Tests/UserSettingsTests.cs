using Vibecheck.Reviewing;
using Xunit;

namespace Vibecheck.Tests;

public class UserSettingsTests
{
    [Fact]
    public void TheThemeIsKeptInTheFileAndReadBackByALaterSession()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings", "settings.json");
        var first = new UserSettings(path);
        Assert.Equal("auto", first.Theme);
        Assert.True(first.SetTheme("dark"));
        Assert.Equal("dark", new UserSettings(path).Theme);

        // A choice the viewer does not offer changes nothing.
        Assert.False(first.SetTheme("blue"));
        Assert.Equal("dark", new UserSettings(path).Theme);

        // Auto is stored too, so that it wins over an older choice.
        Assert.True(first.SetTheme("auto"));
        Assert.Equal("auto", new UserSettings(path).Theme);
    }

    [Fact]
    public void ADamagedFileReadsAsTheDefaults()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal("auto", new UserSettings(path).Theme);
        Assert.True(new UserSettings(path).SetTheme("light"));
        Assert.Equal("light", new UserSettings(path).Theme);
    }
}
