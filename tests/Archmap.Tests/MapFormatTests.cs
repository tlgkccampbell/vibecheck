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
    public void WriterSortsNodesAndGroupsAndOrdersKeys()
    {
        var map = TestData.Map();
        map.Views["v"] = new ViewSpec
        {
            Name = "V", Level = "root",
            Groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal)
            {
                ["z"] = new GroupSpec { Name = "Z", Leaves = ["shared"] },
                ["m"] = new GroupSpec { Name = "M", Purpose = "Most.", Leaves = ["a.x", "a.y", "b"] },
            },
        };
        var text = MapWriter.Write(map);
        Assert.Contains("    \"a.x\": { \"name\": \"X\", \"bind\": [\"A.X.**\"] }", text);
        Assert.Contains("    \"v\": { \"name\": \"V\", \"level\": \"root\", \"groups\": {\n      \"m\": { \"name\": \"M\", \"purpose\": \"Most.\", \"leaves\": [\"a.x\", \"a.y\", \"b\"] },\n      \"z\": { \"name\": \"Z\", \"leaves\": [\"shared\"] }\n    } }\n", text);
        Assert.Equal(text, MapWriter.Write(MapReader.Read(text)));
        Assert.True(text.IndexOf("\"nodes\"", StringComparison.Ordinal) < text.IndexOf("\"views\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"views\"", StringComparison.Ordinal) < text.IndexOf("\"externals\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ReaderRejectsUnknownPropertiesAndWrongTypes()
    {
        const string text = """
            { "version": 0, "solution": "x.sln", "nodes": { "a": { "name": "A", "color": "red" } }, "views": { "v": { "name": "V", "level": "root", "groups": { "g": { "name": "G", "leaves": "a" } } } }, "externals": {}, "edges": [] }
            """;
        var ex = Assert.Throws<MapFormatException>(() => MapReader.Read(text));
        Assert.Contains(ex.Errors, e => e.Contains("unknown property \"color\""));
        Assert.Contains(ex.Errors, e => e.Contains("views.v.groups.g.leaves must be an array of strings"));
        Assert.Contains(ex.Errors, e => e.Contains("unknown property \"edges\" at the top level"));
    }

    [Fact]
    public void ReaderRejectsCommentsTrailingCommasAndEmptyViews()
    {
        Assert.Throws<MapFormatException>(() => MapReader.Read("{ /* c */ \"version\": 0, \"solution\": \"x\" }"));
        Assert.Throws<MapFormatException>(() => MapReader.Read("{ \"version\": 0, \"solution\": \"x\", }"));
        var ex = Assert.Throws<MapFormatException>(() => MapReader.Read("{ \"version\": 0, \"solution\": \"x\", \"views\": { \"v\": { \"name\": \"V\", \"level\": \"root\", \"groups\": {} } } }"));
        Assert.Contains(ex.Errors, e => e.Contains("views.v.groups must not be empty"));
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
