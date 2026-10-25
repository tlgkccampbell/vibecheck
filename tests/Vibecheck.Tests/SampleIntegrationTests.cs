using Vibecheck.Analysis;
using Vibecheck.Bootstrap;
using Vibecheck.Checking;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>End-to-end checks against the Orchard sample. Run the unit tests alone with <c>--filter "FullyQualifiedName!~Sample"</c> for the quick loop.</summary>
[Collection("sample")]
public class SampleIntegrationTests(SampleFixture sample)
{
    private const string Capture = "Orchard.Billing.Core.Payments.CapturePayment";
    private const string Context = "Orchard.Billing.Integrations.Stripe.StripeJsonContext";
    private const string Invoice = "Orchard.Billing.Core.Invoices.Invoice";

    [Fact]
    public void AnalysisListsTypesAndFilesIncludingGeneratedOnes()
    {
        // The test project adds three types in two files.
        Assert.Equal(34, sample.Analysis.Types.Count);
        Assert.Equal(18, sample.Analysis.Files.Count(f => f.Generator is null));
        Assert.Equal(2, sample.Analysis.Types.Count(t => t.Name.StartsWith("System.Text.RegularExpressions.Generated.")));
        Assert.Contains(sample.Analysis.Files, f => f.Path.StartsWith("src/Orchard.Billing/Generated/") && f.Generator!.Contains("JsonSourceGenerator"));
        Assert.Empty(sample.Analysis.Warnings);
        Assert.Empty(sample.Analysis.Unresolvable);
        var known = sample.Analysis.Types.Select(t => t.Name).ToHashSet();
        Assert.All(sample.Analysis.Dependencies, d => { Assert.Contains(d.FromType, known); Assert.Contains(d.ToType, known); });
    }

    [Fact]
    public void CuratedMapPasses()
    {
        var facts = Checker.Run(sample.Map, sample.Analysis, new CheckOptions { MapText = sample.MapText });
        Assert.Empty(facts.Violations);
        Assert.Empty(facts.Warnings);
        Assert.DoesNotContain(facts.Types, t => t.Leaf == ArchitectureMap.Unsorted);
        Assert.Equal(new ExcludedCounts(2, 0), facts.Excluded);
        Assert.Equal(29, facts.Types.Count);
        Assert.DoesNotContain(facts.Dependencies, d => d.To.StartsWith("System."));
    }

    [Fact]
    public void TestTypesArePlacedByTheirSubjectsAndKeepTheirExercises()
    {
        const string session = "Orchard.Auth.Sessions.Session";
        var facts = Checker.Run(sample.Map, sample.Analysis);
        Assert.Equal("3 test types: 1 by name, 1 by references, 1 infrastructure", TestTypes.Placement(facts.Tests));
        var tests = facts.Tests.Types.Single(t => t.Name == "Orchard.Tests.Sessions.SessionTests");
        Assert.Equal(new SubjectFact(session, "auth.sessions", "name"), tests.Subject);
        Assert.Equal(["Has_FindsAGrantedScope", "Has_RejectsAMissingScope"], tests.Methods.Select(m => m.Symbol.Split('.')[^1]));
        Assert.Equal(new SubjectFact(session, "auth.sessions", "references"), facts.Tests.Types.Single(t => t.Name == "Orchard.Tests.Sessions.ScopeChecks").Subject);
        Assert.Null(facts.Tests.Types.Single(t => t.Name == "Orchard.Tests.Sessions.SessionBuilder").Subject);
        Assert.Contains(facts.Tests.Exercises, e => e.Symbol == session + ".Has" && e.Method == "Orchard.Tests.Sessions.SessionTests.Has_FindsAGrantedScope");
        Assert.DoesNotContain(facts.Tests.Exercises, e => e.Symbol.StartsWith("Xunit.", StringComparison.Ordinal) || e.Symbol.StartsWith("Orchard.Tests.", StringComparison.Ordinal));
        // The test builds its session through the builder, so it reaches the session's type through that helper.
        var references = TestReferences.Of(facts.Tests)["Orchard.Tests.Sessions.SessionTests.Has_FindsAGrantedScope"];
        Assert.Equal(new TestReference("direct"), references[session + ".Has"]);
        Assert.Equal(new TestReference("helper", "Orchard.Tests.Sessions.SessionBuilder.With"), references[session]);
        var file = facts.Files.Single(f => f.Path == "tests/Orchard.Tests/Sessions/SessionTests.cs");
        Assert.Equal((true, "auth.sessions"), (file.Test, file.Subject));
        Assert.Equal((true, (string?)null), facts.Files.Where(f => f.Path.EndsWith("SessionBuilder.cs")).Select(f => (f.Test, f.Subject)).Single());
        Assert.DoesNotContain(facts.Types, t => t.Assembly == "Orchard.Tests");
        Assert.DoesNotContain(facts.LeafEdges, e => e.From == ArchitectureMap.Unsorted || e.To == ArchitectureMap.Unsorted);
    }

    [Fact]
    public void PartialTypesAreOneTypeAcrossSourceAndGeneratedFiles()
    {
        var service = sample.Analysis.Types.Single(t => t.Name == "Orchard.Billing.Core.PaymentService");
        Assert.Equal(2, service.Files.Count);
        Assert.Null(service.Generator);
        var context = sample.Analysis.Types.Single(t => t.Name == Context);
        Assert.Contains(context.Files, f => f.Contains("/Generated/"));
        Assert.Contains("JsonSourceGenerator", context.Generator);
        var facts = Checker.Run(sample.Map, sample.Analysis);
        Assert.Equal("billing.integrations.stripe", facts.Types.Single(t => t.Name == Context).Leaf);
        // A reference to a member that exists only in the generated half resolves to the type.
        Assert.Contains(facts.Dependencies, d => d.From == "Orchard.Billing.Integrations.Stripe.StripeClient" && d.To == Context);
        Assert.Contains(facts.LeafEdges, e => e.From == "billing.integrations.stripe" && e.To == "billing.integrations.stripe");
    }

    [Fact]
    public void AttributeBindsInvoiceToTheLedgerAndIsCounted()
    {
        var invoice = sample.Analysis.Types.Single(t => t.Name == Invoice);
        Assert.Equal("billing.ledger", invoice.Attribute);
        var facts = Checker.Run(sample.Map, sample.Analysis);
        var fact = facts.Types.Single(t => t.Name == Invoice);
        Assert.Equal(("billing.ledger", "attribute"), (fact.Leaf, fact.By));
        Assert.Equal(1, facts.Nodes["billing"].Attributed);
        Assert.Equal(0, facts.Nodes["auth"].Attributed);
        var file = facts.Files.Single(f => f.Path.EndsWith("InvoiceGenerator.cs"));
        Assert.Equal(["billing.core.invoices", "billing.ledger"], file.Leaves);
        Assert.DoesNotContain(facts.Dependencies, d => d.To == "Vibecheck.VibecheckAttribute");
    }

    [Fact]
    public void ResolvesReferencesToTheDeclaringType()
    {
        var deps = sample.Analysis.Dependencies.Where(d => d.FromType == Capture).ToList();
        Assert.Contains(deps, d => d.ToType == "Orchard.Auth.Sessions.ISessionReader" && d.Kind == "calls" && d.Symbol.EndsWith("RequireScopeAsync"));
        Assert.Contains(deps, d => d.ToType == "Orchard.Notifications.Dispatch.PaymentEvent" && d.Kind == "constructs");
        Assert.Contains(deps, d => d.ToType == "Orchard.Billing.Integrations.Stripe.StripeClient");
        Assert.DoesNotContain(deps, d => d.ToType == Capture);
        Assert.All(deps, d => Assert.Equal("src/Orchard.Billing/Core/Payments/CapturePayment.cs", d.File));
    }

    [Fact]
    public void DeclarationsAndSitesCoverTheSample()
    {
        const string session = "Orchard.Auth.Sessions.Session";
        var has = sample.Analysis.Declarations.Single(d => d.Symbol == session + ".Has");
        Assert.Equal(("method", session, "public", true, "src/Orchard.Auth/Sessions/Session.cs"), (has.Kind, has.Type, has.Accessibility, has.Exposed, has.File));
        Assert.Contains(sample.Analysis.Declarations, d => d.Symbol == "Orchard.Auth.Sessions.Scope.ReadOrders" && d.Kind == "field");
        // A partial type has one declaration for each of its parts.
        Assert.Equal(2, sample.Analysis.Declarations.Count(d => d.Symbol == "Orchard.Billing.Core.PaymentService" && d.Kind == "type"));
        Assert.Contains(sample.Analysis.Sites, s => s.Symbol == session + ".Scopes" && s.FromType == session && s.ToType == session);
        Assert.Contains(sample.Analysis.Sites, s => s.Symbol == Invoice + "." + Invoice.Split('.')[^1] && s.ToType == Invoice);
        var known = sample.Analysis.Types.Select(t => t.Name).ToHashSet();
        Assert.All(sample.Analysis.Declarations, d => Assert.Contains(d.Type, known));
        Assert.All(sample.Analysis.Sites, s => { Assert.Contains(s.FromType, known); Assert.Contains(s.ToType, known); });
    }

    [Fact]
    public void ResolvesExternalsToTheMostSpecificPattern()
    {
        var facts = Checker.Run(sample.Map, sample.Analysis);
        Assert.Contains(facts.ExternalRefs, e => e.Assembly == "System.Text.Json" && e.External == "json");
        Assert.DoesNotContain(facts.ExternalRefs, e => e.External is null);
    }

    [Fact]
    public void RootEdgesAndTheDispatchTemplatesCycleAreReported()
    {
        var facts = Checker.Run(sample.Map, sample.Analysis);
        var root = facts.Levels["root"];
        Assert.Contains(root.Edges, e => e.From == "billing" && e.To == "auth");
        Assert.Contains(root.Edges, e => e.From == "billing" && e.To == "notifications");
        Assert.Contains(root.Edges, e => e.From == "billing" && e.To == "json");
        Assert.DoesNotContain(root.Edges, e => e.From == "auth" && e.To == "notifications");
        Assert.Empty(root.Cycles);
        var cycle = Assert.Single(facts.Levels["notifications"].Cycles);
        Assert.Equal(["notifications.dispatch", "notifications.templates"], cycle.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Empty(facts.Levels["billing"].Cycles);
    }

    [Fact]
    public void TheByConcernViewRegroupsTheLeaves()
    {
        var facts = Checker.Run(sample.Map, sample.Analysis);
        var view = facts.Views["by-concern"];
        Assert.Equal(["money", "plumbing", "requests"], view.Groups.Keys);
        Assert.Contains(view.Edges, e => e.From == "money" && e.To == "plumbing");
        Assert.Equal(facts.Types.Count, view.Groups.Values.Sum(g => g.Types));
        Assert.True(view.Groups["money"].Internal > 0);
    }

    [Fact]
    public void UnitsFoldHelpersAndCarrySummaries()
    {
        var units = Units.Build(sample.Analysis);
        var capture = units.Single(u => u.Head == Capture);
        Assert.StartsWith("Captures", capture.Summary);
        Assert.Contains(units, u => u.Members.Count > 1);
        Assert.Contains("CapturePayment", Units.Listing(units, sample.Analysis.Types.Count));
    }

    [Fact]
    public void SkeletonFromTheSamplePassesAndBindsEveryAssembly()
    {
        var skeleton = Skeleton.Build(sample.Analysis, "Orchard.slnx");
        Assert.Contains("billing.core.self", skeleton.Nodes.Keys);
        Assert.Contains("billing.core.payments", skeleton.Nodes.Keys);
        Assert.Equal("Orchard.Billing", skeleton.Nodes["billing"].Name);
        Assert.Contains("framework", skeleton.Externals.Keys);
        Assert.Equal(["System.**"], skeleton.Exclude);
        Assert.Equal(["Orchard.Tests::**"], skeleton.Tests);
        Assert.DoesNotContain("tests", skeleton.Nodes.Keys);
        var facts = Checker.Run(skeleton, sample.Analysis);
        Assert.Empty(facts.Warnings);
        Assert.DoesNotContain(facts.ExternalRefs, e => e.External is null);
    }
}
