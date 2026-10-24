using System.Text;
using System.Text.Json;
using Vibecheck.Checking;

namespace Vibecheck.Reviewing;

/// <summary>
/// Represents the reviewer's own settings for the viewer, which outlive a server session: so far the theme choice,
/// <c>auto</c>, <c>light</c> or <c>dark</c>. The browser cannot keep them, since each session listens on a port of
/// its own and the browser keeps a page's storage per port, so the server keeps them in a small file under the cache
/// root, shared by every repository, or only in memory when no file is given.
/// </summary>
public sealed class UserSettings
{
    /// <summary>The theme choices the viewer offers.</summary>
    public static readonly string[] Themes = ["auto", "light", "dark"];

    private readonly string? path;
    private readonly object gate = new();
    private string theme = "auto";

    /// <summary>Opens the settings stored in a file, which is created on the first change, or keeps them in memory when the path is <see langword="null"/>.</summary>
    public UserSettings(string? path)
    {
        this.path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path, Encoding.UTF8), FactsJson.Options);
            if (stored?.Theme is { } t && Themes.Contains(t)) theme = t;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file is read as the defaults, and the next change replaces it.
        }
    }

    /// <summary>Gets the theme choice: <c>auto</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get { lock (gate) return theme; } }

    /// <summary>Sets the theme choice and stores it. Returns <see langword="false"/>, changing nothing, when the value is not a choice the viewer offers.</summary>
    public bool SetTheme(string value)
    {
        if (!Themes.Contains(value)) return false;
        lock (gate)
        {
            theme = value;
            Write();
        }
        return true;
    }

    /// <summary>Writes the settings through a temporary file, so that a reader never sees half of them.</summary>
    private void Write()
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(theme), FactsJson.Options), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The settings as the file holds them.</summary>
    private sealed record Stored(string? Theme);
}
