using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Format;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of traits, pattern discovery, the map's patterns list and the patterns of a changeset. See <c>docs/patterns.md</c>.</summary>
public class PatternTests
{
    private const string Consumer = "implements:A.IConsumer<>";
    private const string Retry = "attribute:A.RetryAttribute";

    /// <summary>A stand-in for a messaging framework and a logging library, in assemblies named as such libraries are.</summary>
    private static readonly Lazy<Microsoft.CodeAnalysis.MetadataReference[]> Libraries = new(() =>
    [
        Snippets.Library("MassTransit", """
            namespace MassTransit
            {
                public interface IConsumer<T> { }
                public class ConsumerBase<T> { }
                public sealed class QueueAttribute : System.Attribute { }
            }
            """),
        Snippets.Library("Microsoft.Extensions.Logging", "namespace Microsoft.Extensions.Logging { public interface ILogger<T> { } }"),
    ]);

    [Fact]
    public void TraitsAreRecordedInOpenFormFromSourceAndReferencedAssemblies()
    {
        var analysis = Snippets.Analyze(new Dictionary<string, string>
        {
            ["A/Orders.cs"] = """
                namespace A
                {
                    public sealed record OrderPlaced(int Id);
                    public interface IUnitOfWork { }
                    public interface IAudited<T> { }
                    public sealed class Clock { }

                    [MassTransit.Queue, System.Obsolete]
                    public sealed class OrderPlacedConsumer : MassTransit.ConsumerBase<OrderPlaced>, MassTransit.IConsumer<OrderPlaced>, IAudited<OrderPlaced>, System.IDisposable
                    {
                        public OrderPlacedConsumer() { }
                        public OrderPlacedConsumer(IUnitOfWork work, Microsoft.Extensions.Logging.ILogger<OrderPlacedConsumer> log, string name) { }
                        public void Dispose() { }
                    }

                    public sealed class Primary(IUnitOfWork work)
                    {
                        public Primary(IUnitOfWork work, Clock clock) : this(work) { }
                    }

                    public sealed class Oops : System.Exception { }
                }
                """,
        }, Libraries.Value);
        var traits = analysis.Types.Single(t => t.Name == "A.OrderPlacedConsumer").Traits!;
        Assert.Equal("MassTransit.ConsumerBase<>", traits.Base!.Type);
        Assert.Equal(["A.OrderPlaced"], traits.Base.Arguments!);
        // An interface of the core library names no role, so IDisposable is not a trait.
        Assert.Equal(["MassTransit.IConsumer<>", "A.IAudited<>"], traits.Interfaces.Select(i => i.Type));
        Assert.All(traits.Interfaces, i => Assert.Equal(["A.OrderPlaced"], i.Arguments!));
        Assert.Equal(["MassTransit.QueueAttribute", "System.ObsoleteAttribute"], traits.Attributes);
        // The constructor with the most parameters says what the type needs, and a type of the core library is no trait.
        Assert.Equal(["A.IUnitOfWork", "Microsoft.Extensions.Logging.ILogger<>"], traits.Injects);
        // A primary constructor wins over one with more parameters, since every other constructor calls it.
        Assert.Equal(["A.IUnitOfWork"], analysis.Types.Single(t => t.Name == "A.Primary").Traits!.Injects);
        // A base class of the core library plays a role.
        Assert.Equal("System.Exception", analysis.Types.Single(t => t.Name == "A.Oops").Traits!.Base!.Type);
        // A record's base is object and its equality interface is the core library's, so it has no traits.
        Assert.Null(analysis.Types.Single(t => t.Name == "A.OrderPlaced").Traits);
    }

    [Fact]
    public void ExternalReferencesKeepTheirKindAndReferencedType()
    {
        var analysis = Snippets.Analyze(new Dictionary<string, string>
        {
            ["A/Consumer.cs"] = "namespace A { public sealed class C : MassTransit.IConsumer<int> { public C(Microsoft.Extensions.Logging.ILogger<C> log) { } } }",
        }, Libraries.Value);
        Assert.Equal(["implements MassTransit.IConsumer<>", "references Microsoft.Extensions.Logging.ILogger<>"],
            analysis.ExternalReferences.Select(e => $"{e.Kind} {e.Type}").Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(analysis.ExternalReferences, e => e.Type == "Microsoft.Extensions.Logging.ILogger<>" && e.Kind == "references");
    }

    [Fact]
    public void ThreeTypesSharingAKeyAreAPatternAndTwoAreNot()
    {
        var patterns = Discover([T("A.C1", Consumer), T("A.C2", Consumer), T("A.C3", Consumer), T("A.H1", Handler), T("A.H2", Handler)]);
        var p = Assert.Single(patterns);
        Assert.Equal((Consumer, "implements IConsumer<>", PatternDiscovery.ByKey), (p.Id, p.Name, p.By));
        Assert.Equal(["A.C1", "A.C2", "A.C3"], p.Instances);
    }

    [Fact]
    public void KeysWithTheSameInstancesAreOnePatternOrderedByKind()
    {
        const string controller = "inherits:A.ControllerBase";
        const string api = "attribute:A.ApiControllerAttribute";
        var p = Assert.Single(Discover([T("A.X", api, controller), T("A.Y", api, controller), T("A.Z", api, controller)]));
        Assert.Equal([controller, api], p.Keys);
        Assert.Equal("inherits ControllerBase", p.Name);
        Assert.Equal(controller + " + " + api, p.Id);
    }

    [Fact]
    public void AnIgnoredKeyDropsItsGroup()
    {
        var map = Snippets.Map();
        map.Patterns.Add(new PatternSpec { Ignore = Consumer });
        Assert.Empty(Discover([T("A.C1", Consumer), T("A.C2", Consumer), T("A.C3", Consumer)], map));
    }

    [Fact]
    public void AMapEntryNamesTheDiscoveredPatternAndDeclaresOneThatNeedsNoMinimum()
    {
        var map = Snippets.Map();
        map.Patterns.Add(new PatternSpec { Id = "consumer", Name = "Consumer", Purpose = "Handles one message.", Keys = [Consumer] });
        map.Patterns.Add(new PatternSpec { Id = "handler", Keys = [Handler] });
        var warnings = new List<string>();
        var patterns = Discover([T("A.C1", Consumer, Retry), T("A.C2", Consumer, Retry), T("A.C3", Consumer, Retry), T("A.H1", Handler)], map, warnings);
        // The entry names the discovered pattern, which keeps the key merged into it.
        var consumer = patterns.Single(p => p.Id == "consumer");
        Assert.Equal(("Consumer", "Handles one message.", PatternDiscovery.ByMap), (consumer.Name, consumer.Purpose, consumer.By));
        Assert.Equal([Consumer, Retry], consumer.Keys);
        var handler = patterns.Single(p => p.Id == "handler");
        Assert.Equal("implements IHandler<>", handler.Name);
        Assert.Equal(["A.H1"], handler.Instances);
        Assert.Equal(2, patterns.Count);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ASuffixKeyIsNeverDiscoveredMatchesInADeclaredPatternAndIsWarnedAboutWhenIgnored()
    {
        var types = new[] { T("A.OrderService"), T("A.UserService"), T("B::A.MailService"), T("A.Clock") };
        Assert.Empty(Discover(types));
        var map = Snippets.Map();
        map.Patterns.Add(new PatternSpec { Id = "service", Keys = ["suffix:Service"] });
        map.Patterns.Add(new PatternSpec { Ignore = "suffix:Handler" });
        var warnings = new List<string>();
        var p = Assert.Single(Discover(types, map, warnings));
        Assert.Equal(["A.OrderService", "A.UserService", "B::A.MailService"], p.Instances);
        Assert.Equal("suffix Service", p.Name);
        Assert.Contains(warnings, w => w.Contains("ignoring \"suffix:Handler\" drops nothing"));
    }

    [Fact]
    public void AnEntryWhoseKeysNoTypeCarriesIsWarnedAbout()
    {
        var map = Snippets.Map();
        map.Patterns.Add(new PatternSpec { Id = "consumer", Keys = [Consumer] });
        var warnings = new List<string>();
        var p = Assert.Single(Discover([T("A.Clock")], map, warnings));
        Assert.Empty(p.Instances);
        Assert.Contains(warnings, w => w.Contains("no type carries every key of consumer"));
    }

    [Fact]
    public void AGeneratedTypeIsNeverAnInstance()
    {
        var generated = new HashSet<string>(StringComparer.Ordinal) { "src/A.C3.cs" };
        Assert.Empty(PatternDiscovery.Discover(Snippets.Map(), [T("A.C1", Consumer), T("A.C2", Consumer), T("A.C3", Consumer)], generated, []));
    }

    [Fact]
    public void ATestTypeAndANestedTypeAreNeverInstances()
    {
        const string bus = "namespace A { public interface IConsumer<T> { } }";
        var facts = Snippets.Facts(new Dictionary<string, string>
        {
            ["A/Bus.cs"] = bus,
            ["A/C1.cs"] = "namespace A { public sealed class C1 : IConsumer<int> { } }",
            ["A/C2.cs"] = "namespace A { public sealed class C2 : IConsumer<int> { } }",
            ["A/Nested.cs"] = "namespace A { public static class Nested { public sealed class N1 : IConsumer<int> { } public sealed class N2 : IConsumer<long> { } } }",
            ["tests/FakeConsumer.cs"] = "namespace A.Tests { public sealed class FakeConsumer : A.IConsumer<int> { } }",
        });
        Assert.Empty(facts.Patterns);
        Assert.Null(facts.Types.Single(t => t.Name == "A.Nested").Traits);
    }

    [Fact]
    public void TheCheckFindsPatternsAndReportsTheirInstances()
    {
        var facts = Snippets.Facts(Consumers(4));
        var p = Assert.Single(facts.Patterns);
        Assert.Equal(["A.C1", "A.C2", "A.C3", "A.C4"], p.Instances);
        Assert.Equal(["pattern implements IConsumer<>: 4 instances, 0 norms, 0.0 departures each (implements:A.IConsumer<>)"], PatternDiscovery.Report(facts.Patterns));
    }

    [Fact]
    public void AChangeReportsItsAddedAndChangedInstancesUnderTheMostSpecificPattern()
    {
        // At base, four consumers, three of which retry, so the types that retry are a pattern inside the consumers.
        var before = Consumers(4, retrying: 3);
        var after = new Dictionary<string, string>(before)
        {
            ["A/C4.cs"] = "namespace A { public sealed class C4 : IConsumer<int> { public void Consume() { } } }",
            ["A/C5.cs"] = "namespace A { [Retry] public sealed class C5 : IConsumer<int> { } }",
        };
        var cs = Snippets.Changeset(before, after);
        Assert.Equal([Retry, Consumer], cs.Patterns.Select(p => p.Id).Order(StringComparer.Ordinal));
        var consumer = cs.Patterns.Single(p => p.Id == Consumer);
        var retrying = cs.Patterns.Single(p => p.Id == Retry);
        Assert.Equal((4, 3), (consumer.Count, retrying.Count));
        // The added consumer retries, so it is reported under the types that retry, and the consumers list it among the others.
        Assert.Equal([("A.C5", "added")], retrying.Instances.Select(i => (i.Type, i.Status)));
        Assert.Equal([("A.C4", "changed")], consumer.Instances.Select(i => (i.Type, i.Status)));
        Assert.Equal(["A.C1", "A.C2", "A.C3", "A.C5"], consumer.Others.Select(o => o.Type));
        Assert.Equal(["A.C1", "A.C2", "A.C3"], retrying.Others.Select(o => o.Type));
        Assert.All(consumer.Others, o => Assert.Equal("a", o.Leaf));
    }

    [Fact]
    public void APatternWithoutAnAddedOrChangedInstanceIsNotInTheChangeset()
    {
        var before = Consumers(3);
        var after = new Dictionary<string, string>(before) { ["B/Clock.cs"] = "namespace B { public sealed class Clock { public int Now() => 1; } }" };
        Assert.Empty(Snippets.Changeset(before, after).Patterns);
    }

    [Fact]
    public void ThePatternsListRoundTripsInCanonicalForm()
    {
        var map = TestData.Map();
        map.Patterns.Add(new PatternSpec { Id = "consumer", Name = "Consumer", Purpose = "Handles one message.", Keys = ["implements:MassTransit.IConsumer<>"] });
        map.Patterns.Add(new PatternSpec { Ignore = "implements:MediatR.INotification" });
        var text = MapWriter.Write(map);
        Assert.Contains("""
              "patterns": [
                { "id": "consumer", "name": "Consumer", "purpose": "Handles one message.", "keys": ["implements:MassTransit.IConsumer<>"] },
                { "ignore": "implements:MediatR.INotification" }
              ],

            """.ReplaceLineEndings("\n"), text);
        Assert.Equal(text, MapWriter.Write(MapReader.Read(text)));
        Assert.DoesNotContain("\"patterns\"", MapWriter.Write(TestData.Map()));
    }

    [Fact]
    public void TheReaderRejectsMalformedPatternEntries()
    {
        const string text = """
            { "version": 0, "solution": "x.sln", "patterns": [
              { "id": "a", "keys": ["implement:X"] },
              { "keys": ["suffix:Handler"] },
              { "id": "b", "keys": ["suffix:Handler"], "ignore": "suffix:Handler" },
              { "id": "a", "keys": ["implements:X"] },
              { "id": "c.d", "keys": ["attribute:"] }
            ], "nodes": {}, "views": {}, "externals": {} }
            """;
        var ex = Assert.Throws<MapFormatException>(() => MapReader.Read(text));
        Assert.Contains(ex.Errors, e => e.StartsWith("patterns[0]: \"implement:X\" is not a key"));
        Assert.Contains(ex.Errors, e => e == "patterns[1].id is required with keys");
        Assert.Contains(ex.Errors, e => e == "patterns[2] must have either keys or ignore");
        Assert.Contains(ex.Errors, e => e == "patterns[3].id \"a\" is used by another pattern");
        Assert.Contains(ex.Errors, e => e == "patterns[4].id \"c.d\" is not a single-segment identifier");
        Assert.Contains(ex.Errors, e => e == "patterns[4]: \"attribute:\" names no type");
    }

    private const string Handler = "implements:A.IHandler<>";

    /// <summary>Returns the files of consumers <c>A.C1</c> to <c>A.Cn</c>, each in its own file, the first of them carrying <c>[Retry]</c>.</summary>
    private static Dictionary<string, string> Consumers(int count, int retrying = 0)
    {
        var files = new Dictionary<string, string>
        {
            ["A/Bus.cs"] = "namespace A { public interface IConsumer<T> { } public sealed class RetryAttribute : System.Attribute { } }",
        };
        for (var i = 1; i <= count; i++)
            files[$"A/C{i}.cs"] = $"namespace A {{ {(i <= retrying ? "[Retry] " : "")}public sealed class C{i} : IConsumer<int> {{ }} }}";
        return files;
    }

    /// <summary>Returns a type of leaf <c>a</c> in a file of its own that carries the keys given, as traits that give it those keys.</summary>
    private static TypeFact T(string name, params string[] keys)
    {
        TraitType[] Of(string kind) => keys.Where(k => PatternKeys.KindOf(k) == kind).Select(k => new TraitType(PatternKeys.ValueOf(k))).ToArray();
        var traits = keys.Length == 0 ? null
            : new TypeTraits(Of(PatternKeys.Inherits).FirstOrDefault(), Of(PatternKeys.Implements), Of(PatternKeys.Attribute).Select(t => t.Type).ToList(), []);
        return new TypeFact(name, "Snippets", "a", "pattern", [$"src/{name}.cs"], Traits: traits);
    }

    private static List<PatternFact> Discover(IReadOnlyList<TypeFact> types, ArchitectureMap? map = null, List<string>? warnings = null) =>
        PatternDiscovery.Discover(map ?? Snippets.Map(), types, new HashSet<string>(), warnings ?? []);
}
