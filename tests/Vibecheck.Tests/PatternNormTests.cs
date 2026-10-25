using Vibecheck.Checking;
using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of features, norms, departures, companions and unusual traits. See <c>docs/patterns.md</c> section 5.</summary>
public class PatternNormTests
{
    private const string Consumer = "implements:A.IConsumer<>";
    private const string Work = "injects:A.IUnitOfWork";

    private const string Bus = """
        namespace A
        {
            public interface IConsumer<T> { }
            public interface IUnitOfWork { }
            public sealed class RetryAttribute : System.Attribute { }
            public sealed class AuditAttribute : System.Attribute { }
            public sealed class Clock { }
        }
        """;

    [Fact]
    public void AFeatureOfEightyPercentOfFiveOrMoreInstancesIsANormAndAPatternOfFourHasNone()
    {
        // Four of five consumers take a unit of work.
        var facts = Snippets.Facts(Consumers(5, working: 4));
        var p = Assert.Single(facts.Patterns);
        Assert.Equal([new NormFact(Work, 4)], p.Norms);
        Assert.Equal(0.2, p.MeanDepartures);
        Assert.Equal(["pattern implements IConsumer<>: 5 instances, 1 norm, 0.2 departures each (implements:A.IConsumer<>)"], PatternDiscovery.Report(facts.Patterns));

        // Three of four is too few instances, and three of five too small a share.
        Assert.Empty(Assert.Single(Snippets.Facts(Consumers(4, working: 4)).Patterns).Norms);
        Assert.Empty(Assert.Single(Snippets.Facts(Consumers(5, working: 3)).Patterns).Norms);
    }

    [Fact]
    public void AnAddedInstanceDepartsFromEveryNormItLacksAndAChangedOneOnlyFromThoseItHadBefore()
    {
        // At base, five of six consumers take a unit of work, and C6 never did.
        var before = Consumers(6, working: 5);
        var after = new Dictionary<string, string>(before)
        {
            ["A/C1.cs"] = Source(1, working: false),
            ["A/C6.cs"] = Source(6, working: false, extra: "public void Flush() { }"),
            ["A/C7.cs"] = Source(7, working: false),
        };
        var p = Assert.Single(Snippets.Changeset(before, after).Patterns);
        Assert.Equal([new NormChange(Work, 5, "injects IUnitOfWork", "no IUnitOfWork")], p.Norms);
        var instances = p.Instances.ToDictionary(i => i.Type);
        Assert.Equal([Work], instances["A.C7"].Departures);
        Assert.Equal([Work], instances["A.C1"].Departures);
        // C6 lacked the unit of work before the change, so its lack is the system's and not the change's doing.
        Assert.Empty(instances["A.C6"].Departures);
        Assert.Equal([Work], instances["A.C6"].Lacks);
    }

    [Fact]
    public void ADepartingInstanceIsAFirstTierStepAndEveryInstanceIsCountedOnItsLeaf()
    {
        var before = Consumers(5, working: 5);
        var after = new Dictionary<string, string>(before)
        {
            ["A/C6.cs"] = Source(6, working: false),
            ["A/C7.cs"] = Source(7, working: true),
        };
        var cs = Snippets.Changeset(before, after);
        var step = Assert.Single(cs.Path, s => s.Kind == "pattern");
        Assert.Equal(("first", "A.C6", "C6 departs from implements IConsumer<>: no IUnitOfWork"), (step.Tier, step.Target, step.Signal));
        Assert.Equal(["A/C6.cs"], step.Files);
        Assert.Equal(Consumer, step.Pattern);
        Assert.Contains("2 new instances of implements IConsumer<>", Assert.Single(cs.Path, s => s.Id == "node:a").Signal);
    }

    [Fact]
    public void InstancesSharingAKeyTypeArgumentAreCompanionsAndACompanionPatternIsANorm()
    {
        const string kinds = """
            namespace A
            {
                public interface IHandler<T> { }
                public interface IValidator<T> { }
            }
            """;
        var before = new Dictionary<string, string> { ["A/Kinds.cs"] = kinds };
        for (var i = 1; i <= 5; i++)
        {
            before[$"A/Cmd{i}.cs"] = $"namespace A {{ public sealed record Cmd{i}; public sealed class Cmd{i}Handler : IHandler<Cmd{i}> {{ }} }}";
            if (i <= 4) before[$"A/Cmd{i}Validator.cs"] = $"namespace A {{ public sealed class Cmd{i}Validator : IValidator<Cmd{i}> {{ }} }}";
        }
        const string handler = "implements:A.IHandler<>", validator = "implements:A.IValidator<>";
        var facts = Snippets.Facts(before);
        Assert.Contains(new NormFact(PatternFeatures.Companion + validator, 4), facts.Patterns.Single(p => p.Id == handler).Norms);

        var after = new Dictionary<string, string>(before)
        {
            ["A/Cmd6.cs"] = "namespace A { public sealed record Cmd6; public sealed class Cmd6Handler : IHandler<Cmd6> { } }",
            ["A/Cmd7.cs"] = "namespace A { public sealed record Cmd7; public sealed class Cmd7Handler : IHandler<Cmd7> { } public sealed class Cmd7Validator : IValidator<Cmd7> { } }",
        };
        var p = Snippets.Changeset(before, after).Patterns.Single(p => p.Id == handler);
        var instances = p.Instances.ToDictionary(i => i.Type);
        Assert.Equal([PatternFeatures.Companion + validator], instances["A.Cmd6Handler"].Departures);
        Assert.Equal("no companion implements IValidator<>", p.Norms.Single(n => n.Feature.StartsWith(PatternFeatures.Companion)).Absence);
        Assert.Empty(instances["A.Cmd7Handler"].Departures);
        Assert.Equal("A.Cmd7Validator", instances["A.Cmd7Handler"].Companions[validator]);
    }

    [Fact]
    public void ATypeOfTheCoreLibraryLinksNoCompanions()
    {
        const string kinds = "namespace A { public interface IQuery<T> { } public interface IResult<T> { } }";
        var before = new Dictionary<string, string> { ["A/Kinds.cs"] = kinds };
        for (var i = 1; i <= 3; i++)
        {
            before[$"A/Q{i}.cs"] = $"namespace A {{ public sealed class Q{i} : IQuery<int> {{ }} }}";
            before[$"A/R{i}.cs"] = $"namespace A {{ public sealed class R{i} : IResult<int> {{ }} }}";
        }
        var after = new Dictionary<string, string>(before) { ["A/Q4.cs"] = "namespace A { public sealed class Q4 : IQuery<int> { } }" };
        var instance = Assert.Single(Snippets.Changeset(before, after).Patterns.Single(p => p.Id == "implements:A.IQuery<>").Instances);
        Assert.Empty(instance.Companions);
    }

    [Fact]
    public void ATraitNoOtherInstanceHasIsUnusualAndNotADepartureWhileAnInjectedTypeNoOtherHasIsNeither()
    {
        var before = Consumers(5, working: 5);
        var after = new Dictionary<string, string>(before)
        {
            ["A/C6.cs"] = "namespace A { [Audit] public sealed class C6 : IConsumer<int> { public C6(IUnitOfWork work, Clock clock) { } } }",
        };
        var instance = Assert.Single(Assert.Single(Snippets.Changeset(before, after).Patterns).Instances);
        Assert.Equal(["attribute:A.AuditAttribute"], instance.Unusual);
        Assert.Empty(instance.Departures);
    }

    /// <summary>Returns the files of consumers <c>A.C1</c> to <c>A.Cn</c>, each in its own file, the first of them taking a unit of work.</summary>
    private static Dictionary<string, string> Consumers(int count, int working)
    {
        var files = new Dictionary<string, string> { ["A/Bus.cs"] = Bus };
        for (var i = 1; i <= count; i++) files[$"A/C{i}.cs"] = Source(i, i <= working);
        return files;
    }

    /// <summary>Returns the source of consumer <c>A.Cn</c>, which takes a unit of work when told to, with an extra member when one is given.</summary>
    private static string Source(int n, bool working, string extra = "") =>
        $"namespace A {{ public sealed class C{n} : IConsumer<int> {{ {(working ? $"public C{n}(IUnitOfWork work) {{ }} " : "")}{extra} }} }}";
}
