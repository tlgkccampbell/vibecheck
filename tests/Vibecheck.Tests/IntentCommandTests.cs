using Vibecheck.Cli;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of <c>vibecheck intent</c>, which checks a draft description's intents without a build. See <c>docs/intent.md</c> section 7.3.</summary>
public class IntentCommandTests
{
    private static readonly ArchitectureMap Map = new()
    {
        Solution = "App.slnx",
        Nodes = new(StringComparer.Ordinal)
        {
            ["billing"] = new() { Name = "Billing" },
            ["billing.api"] = new() { Name = "API", Bind = ["App.Billing.Api.**"] },
            ["billing.ledger"] = new() { Name = "Ledger", Bind = ["App.Billing.Ledger.**"] },
        },
        Externals = new(StringComparer.Ordinal) { ["json"] = new() { Name = "System.Text.Json", Bind = ["System.Text.Json"] } },
    };

    [Fact]
    public void ATextWithoutAnIntentSectionFails()
    {
        using var temp = new TempDirectory();
        var (code, output, _) = Run("## Summary\n\n- **A.** `billing`\n", Map, temp.Path);

        Assert.Equal(1, code);
        Assert.Contains("no Intent section", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemWithoutAClaimFails()
    {
        using var temp = new TempDirectory();
        var (code, output, _) = Run("## Intent\n\n- **A.** `billing`\n  Why: b.\n- `billing.api`\n", Map, temp.Path);

        Assert.Equal(1, code);
        Assert.Contains("line 5: the item has no claim", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsUnknownNodesAndAbsentFilesAsWarnings()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Present.cs"), "");
        var text = """
            ## Intent

            - **A.** `billing.ledger`, `json`, `billing.api → billing.ledger`, `Present.cs`, `App.Billing.Api.Thing`
              Why: b.
            - **B.** `billing.leger`, `billing.api → billing.nowhere`, `src/Absent.cs`
              Why: c.
            - **C.**
            """;
        var (code, output, error) = Run(text, Map, temp.Path);

        Assert.Equal(0, code);
        Assert.Contains("node billing.ledger: a node of the map", output, StringComparison.Ordinal);
        Assert.Contains("node json: an external of the map", output, StringComparison.Ordinal);
        Assert.Contains("edge billing.api → billing.ledger: both ends are in the map", output, StringComparison.Ordinal);
        Assert.Contains("file Present.cs: exists", output, StringComparison.Ordinal);
        Assert.Contains("type App.Billing.Api.Thing: unchecked", output, StringComparison.Ordinal);
        Assert.Contains("no anchors", output, StringComparison.Ordinal);
        Assert.Equal(
            [
                "warning: line 5: `billing.leger` names no node or external of the map",
                "warning: line 5: `billing.api → billing.nowhere` has an end, billing.nowhere, that names no node or external of the map",
                "warning: line 5: `src/Absent.cs` names no file in the working tree",
                "warning: line 7: the intent has no Why:",
            ],
            error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains("ok: 3 intent(s) with 8 anchor(s) in the section at line 1, 4 warning(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAMapANodeAnchorIsAWarning()
    {
        using var temp = new TempDirectory();
        var (code, _, error) = Run("## Intent\n- **A.** `billing`\n  Why: b.\n", null, temp.Path);

        Assert.Equal(0, code);
        Assert.Contains("`billing` names a node, but there is no map", error, StringComparison.Ordinal);
    }

    [Fact]
    public void WithABaseMarksTheFilesChangedSinceIt()
    {
        using var repo = new TempRepo();
        repo.Write("src/Same.cs", "class Same {}");
        repo.Write("src/Edited.cs", "class Edited {}");
        repo.Write("src/Gone.cs", "class Gone {}");
        var baseCommit = repo.Commit("base");
        repo.Write("src/Edited.cs", "class Edited { int x; }");
        File.Delete(Path.Combine(repo.Path, "src/Gone.cs"));
        repo.Write("src/New.cs", "class New {}");
        var text = "## Intent\n- **A.** `src/Same.cs`, `src/Edited.cs`, `src/Gone.cs`, `src/New.cs`\n  Why: b.\n";

        var (code, output, error) = Run(text, null, repo.Path, baseCommit);

        Assert.Equal(0, code);
        Assert.Contains($"file src/Same.cs: exists, unchanged since {baseCommit}", output, StringComparison.Ordinal);
        Assert.Contains($"file src/Edited.cs: exists, changed since {baseCommit}", output, StringComparison.Ordinal);
        Assert.Contains($"file src/Gone.cs: deleted since {baseCommit}", output, StringComparison.Ordinal);
        Assert.Contains($"file src/New.cs: exists, changed since {baseCommit}", output, StringComparison.Ordinal);
        Assert.Equal("", error);
    }

    private static (int Code, string Output, string Error) Run(string text, ArchitectureMap? map, string top, string? baseRevision = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = IntentCommand.Run(text, map, top, baseRevision, output, error);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }
}
