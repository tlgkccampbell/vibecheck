using Archmap.Analysis;
using Archmap.Bootstrap;
using Archmap.Checking;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

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
        Assert.Equal(31, sample.Analysis.Types.Count);
        Assert.Equal(16, sample.Analysis.Files.Count(f => f.Generator is null));
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
        Assert.All(facts.Types, t => Assert.NotNull(t.Leaf));
        Assert.Equal(new ExcludedCounts(2, 0), facts.Excluded);
        Assert.Equal(29, facts.Types.Count);
        Assert.DoesNotContain(facts.Dependencies, d => d.To.StartsWith("System."));
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
        Assert.Contains(facts.Dependencies, d => d.From == "Orchard.Billing.Integrations.Stripe.StripeClient" && d.To == Context && d.Level == "billing.integrations.stripe");
    }

    [Fact]
    public void AttributeBindsInvoiceToTheLedgerAndIsCounted()
    {
        var invoice = sample.Analysis.Types.Single(t => t.Name == Invoice);
        Assert.Equal("billing.ledger", invoice.Attribute);
        var facts = Checker.Run(sample.Map, sample.Analysis);
        var fact = facts.Types.Single(t => t.Name == Invoice);
        Assert.Equal(("billing.ledger", "attribute"), (fact.Leaf, fact.By));
        Assert.Equal(1, facts.Metrics["billing"].Attributed);
        Assert.Equal(0, facts.Metrics["auth"].Attributed);
        var file = facts.Files.Single(f => f.Path.EndsWith("InvoiceGenerator.cs"));
        Assert.Equal(["billing.core.invoices", "billing.ledger"], file.Leaves);
        Assert.DoesNotContain(facts.Dependencies, d => d.To == "Archmap.ArchmapAttribute");
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
    public void ResolvesExternalsToTheMostSpecificPattern()
    {
        var facts = Checker.Run(sample.Map, sample.Analysis);
        Assert.Contains(facts.ExternalRefs, e => e.Assembly == "System.Text.Json" && e.External == "json");
        Assert.DoesNotContain(facts.ExternalRefs, e => e.External is null);
    }

    [Fact]
    public void RemovingADeclaredEdgeFailsPermissionAndTheProposalRestoresIt()
    {
        var map = sample.Map.Clone();
        map.Edges.RemoveAll(e => e.From == "billing" && e.To == "auth");
        var facts = Checker.Run(map, sample.Analysis);
        var v = Assert.Single(facts.Violations);
        Assert.Equal("permission", v.Rule);
        Assert.Contains("billing → auth", v.Message);
        var repaired = Proposals.Apply(map, [v.Proposal!]);
        Assert.Empty(Checker.Run(repaired, sample.Analysis).Violations);
    }

    [Fact]
    public void ADeadEdgeFailsRealization()
    {
        var map = sample.Map.Clone();
        map.Edges.Add(new EdgeSpec { From = "auth", To = "notifications" });
        var v = Assert.Single(Checker.Run(map, sample.Analysis).Violations);
        Assert.Equal("realization", v.Rule);
    }

    [Fact]
    public void MetricsReportTheDispatchTemplatesCycle()
    {
        var facts = Checker.Run(sample.Map, sample.Analysis);
        var cycle = Assert.Single(facts.Metrics["notifications"].Cycles);
        Assert.Equal(new[] { "notifications.dispatch", "notifications.templates" }, cycle.OrderBy(c => c).ToArray());
        Assert.Empty(facts.Metrics["billing"].Cycles);
    }

    [Fact]
    public void SkeletonFromTheSamplePassesAndBindsEveryAssembly()
    {
        var skeleton = Skeleton.Build(sample.Analysis, "Orchard.slnx");
        Assert.Contains("billing.core.self", skeleton.Nodes.Keys);
        Assert.Contains("billing.core.payments", skeleton.Nodes.Keys);
        Assert.Equal("Orchard.Billing", skeleton.Nodes["billing"].Name);
        Assert.Contains("framework", skeleton.Externals.Keys);
        var facts = Checker.Run(skeleton, sample.Analysis);
        Assert.Empty(facts.Violations);
        Assert.DoesNotContain(facts.ExternalRefs, e => e.External is null);
    }
}
