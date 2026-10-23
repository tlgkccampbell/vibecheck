using Archmap.Bootstrap;
using Archmap.Checking;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

/// <summary>End-to-end checks against the Orchard sample. Run the unit tests alone with <c>--filter "FullyQualifiedName!~Sample"</c> for the quick loop.</summary>
[Collection("sample")]
public class SampleIntegrationTests(SampleFixture sample)
{
    [Fact]
    public void AnalysisSeesEveryCompiledFileAndNoGeneratedOnes()
    {
        Assert.Equal(15, sample.Analysis.Files.Count);
        Assert.All(sample.Analysis.Files, f => Assert.StartsWith("src/Orchard.", f.Path));
        Assert.DoesNotContain(sample.Analysis.Files, f => f.Path.Contains("/Generated/"));
        Assert.Empty(sample.Analysis.Warnings);
    }

    [Fact]
    public void GeneratedCodeOnDiskIsGeneratedNotSource()
    {
        // Billing writes generator output under Generated/. A reference to a symbol that
        // exists only there is unresolvable and names the generator; a reference to a
        // partial type with a source declaration resolves to that declaration.
        var listed = sample.Analysis.Files.Select(f => f.Path).ToHashSet();
        Assert.All(sample.Analysis.Dependencies, d => Assert.Contains(d.ToFile, listed));
        var u = Assert.Single(sample.Analysis.Unresolvable, u => u.Symbol.EndsWith("StripeJsonContext.Default"));
        Assert.Contains("JsonSourceGenerator", u.Generator);
        Assert.Equal("src/Orchard.Billing/Integrations/Stripe/StripeClient.cs", u.FromFile);
    }

    [Fact]
    public void CuratedMapPasses()
    {
        var facts = Checker.Run(sample.Map, sample.Analysis, new CheckOptions { MapText = sample.MapText });
        Assert.Empty(facts.Violations);
        Assert.All(facts.Files, f => Assert.NotNull(f.Leaf));
    }

    [Fact]
    public void ResolvesMembersToTheirDeclaringFile()
    {
        var capture = "src/Orchard.Billing/Core/Payments/CapturePayment.cs";
        var deps = sample.Analysis.Dependencies.Where(d => d.FromFile == capture).ToList();
        Assert.Contains(deps, d => d.ToFile == "src/Orchard.Auth/Sessions/ISessionReader.cs" && d.Kind == "calls" && d.Symbol.EndsWith("RequireScopeAsync"));
        Assert.Contains(deps, d => d.ToFile == "src/Orchard.Notifications/Dispatch/Dispatcher.cs" && d.Kind == "constructs" && d.Type == "PaymentEvent");
        Assert.Contains(deps, d => d.ToFile == "src/Orchard.Billing/Integrations/Stripe/StripeClient.cs");
        Assert.DoesNotContain(deps, d => d.ToFile == capture);
    }

    [Fact]
    public void DetectsPartialTypeAcrossFilesButNotAcrossGeneratedParts()
    {
        var partial = Assert.Single(sample.Analysis.PartialTypes);
        Assert.Equal("Orchard.Billing.Core.PaymentService", partial.Name);
        Assert.Equal(2, partial.Files.Count);
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
        Assert.Equal("Orchard.Billing", skeleton.Nodes["billing"].Name);
        Assert.Contains("framework", skeleton.Externals.Keys);
        var facts = Checker.Run(skeleton, sample.Analysis);
        Assert.Empty(facts.Violations);
        Assert.DoesNotContain(facts.ExternalRefs, e => e.External is null);
    }
}
