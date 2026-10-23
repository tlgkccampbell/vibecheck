using Archmap.Format;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class MapFormatTests
{
    [Fact]
    public void WriterIsCanonicalAndIdempotent()
    {
        var text = MapWriter.Write(TestData.Map());
        var reread = MapReader.Read(text);
        Assert.Equal(text, MapWriter.Write(reread));
        Assert.EndsWith("}\n", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void WriterSortsNodesAndEdgesAndOrdersKeys()
    {
        var map = TestData.Map();
        map.Edges.Clear();
        map.Edges.Add(new EdgeSpec { From = "b", To = "shared", Payload = "P", Label = "L", Asserted = true });
        map.Edges.Add(new EdgeSpec { From = "a", To = "shared" });
        var text = MapWriter.Write(map);
        var edgesStart = text.IndexOf("\"edges\"", StringComparison.Ordinal);
        Assert.True(text.IndexOf("\"from\": \"a\"", edgesStart, StringComparison.Ordinal) < text.IndexOf("\"from\": \"b\"", edgesStart, StringComparison.Ordinal));
        Assert.Contains("{ \"from\": \"b\", \"to\": \"shared\", \"label\": \"L\", \"payload\": \"P\", \"asserted\": true }", text);
        Assert.Contains("    \"a.x\": { \"name\": \"X\", \"bind\": [\"A.X.**\"] }", text);
    }

    [Fact]
    public void ReaderRejectsUnknownPropertiesAndWrongTypes()
    {
        const string text = """
            { "version": 0, "solution": "x.sln", "nodes": { "a": { "name": "A", "color": "red" } }, "externals": {}, "edges": [ { "from": "a", "to": "b", "asserted": "yes" } ], "policies": {} }
            """;
        var ex = Assert.Throws<MapFormatException>(() => MapReader.Read(text));
        Assert.Contains(ex.Errors, e => e.Contains("unknown property \"color\""));
        Assert.Contains(ex.Errors, e => e.Contains("asserted must be a boolean"));
    }

    [Fact]
    public void ReaderRejectsCommentsTrailingCommasAndLeafPolicies()
    {
        Assert.Throws<MapFormatException>(() => MapReader.Read("{ /* c */ \"version\": 0, \"solution\": \"x\" }"));
        Assert.Throws<MapFormatException>(() => MapReader.Read("{ \"version\": 0, \"solution\": \"x\", }"));
        var ex = Assert.Throws<MapFormatException>(() => MapReader.Read("{ \"version\": 0, \"solution\": \"x\", \"nodes\": { \"a\": { \"name\": \"A\", \"bind\": [\"a/**\"], \"edges\": \"declared\" } } }"));
        Assert.Contains(ex.Errors, e => e.Contains("cannot carry an edges policy"));
    }

    [Fact]
    public void ReaderRejectsOtherVersions()
    {
        var ex = Assert.Throws<MapFormatException>(() => MapReader.Read("{ \"version\": 1, \"solution\": \"x\" }"));
        Assert.Contains(ex.Errors, e => e.Contains("version 1"));
    }

    [Fact]
    public void LayoutRoundTripsAndReportsUnknownIdentifiers()
    {
        const string text = """
            { "version": 0, "views": { "root": { "a": [1, 2, 3, 4], "zzz": [0, 0, 1, 1] }, "a": { "a.x": [5, 6, 7.5, 8] } } }
            """;
        var errors = new List<string>();
        var layout = LayoutFile.Read(text, errors)!;
        Assert.Empty(errors);
        Assert.Equal(new[] { "zzz" }, layout.UnknownIdentifiers(TestData.Map()).ToArray());
        var canonical = layout.Write();
        Assert.Equal(canonical, LayoutFile.Read(canonical, errors)!.Write());
        Assert.Contains("\"a.x\": [5, 6, 7.5, 8]", canonical);
    }

    [Fact]
    public void LayoutRejectsMalformedEntries()
    {
        var errors = new List<string>();
        LayoutFile.Read("{ \"version\": 0, \"views\": { \"root\": { \"a\": [1, 2] } }, \"extra\": 1 }", errors);
        Assert.Contains(errors, e => e.Contains("must be [x, y, w, h]"));
        Assert.Contains(errors, e => e.Contains("unknown property \"extra\""));
    }
}
