using System.Reflection;
using System.Text;

namespace Archmap.Assist;

/// <summary>
/// Reads the prompts and answer schemas embedded in the library, from <c>Assist/Prompts</c>. A prompt is a Markdown
/// template whose <c>{{name}}</c> placeholders are filled in before it is sent.
/// </summary>
public static class Prompts
{
    /// <summary>Returns the text of one embedded prompt or schema file.</summary>
    public static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("prompts/" + name)
            ?? throw new FileNotFoundException("the prompt is not embedded", name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>Returns a template with each <c>{{name}}</c> placeholder replaced by its value.</summary>
    public static string Fill(string template, params (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values) template = template.Replace("{{" + name + "}}", value);
        return template;
    }
}
