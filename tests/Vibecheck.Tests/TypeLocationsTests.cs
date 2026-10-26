using Vibecheck.Diffing;
using Vibecheck.GitHub;
using Xunit;

namespace Vibecheck.Tests;

public class TypeLocationsTests
{
    [Fact]
    public void AChangesetLocatesEveryTypeAtHeadWhetherOrNotTheChangeTouchesIt()
    {
        var before = new Dictionary<string, string>
        {
            ["A/Order.cs"] = "namespace A;\n\npublic sealed class Order\n{\n    public sealed class Line { }\n}\n",
            ["A/Gone.cs"] = "namespace A;\npublic sealed class Gone { }\n",
            ["A/Ledger.cs"] = "namespace A;\npublic partial class Ledger\n{\n    public void Post() { }\n    public void Void() { }\n}\n",
            ["A/Ledger.Small.cs"] = "namespace A;\npublic partial class Ledger { }\n",
            ["tests/OrderTests.cs"] = "namespace Snippets.Tests;\npublic sealed class OrderTests\n{\n    [Xunit.Fact]\n    public void Works() { new A.Order(); }\n}\n",
        };
        var after = new Dictionary<string, string>(before) { ["A/New.cs"] = "namespace A;\npublic sealed class New { }\n" };
        after.Remove("A/Gone.cs");
        var locations = Snippets.Changeset(before, after).Locations;
        Assert.Equal(
            [
                new TypeLocation("A.Ledger", "A/Ledger.cs", 2),
                new TypeLocation("A.New", "A/New.cs", 2),
                new TypeLocation("A.Order", "A/Order.cs", 3),
                new TypeLocation("A.Order.Line", "A/Order.cs", 5),
                new TypeLocation("Snippets.Tests.OrderTests", "tests/OrderTests.cs", 2),
            ],
            locations);
    }

    [Theory]
    [InlineData("github.com", "https://github.com/octo/app/blob/abc123/")]
    [InlineData("git.example.com", "https://git.example.com/octo/app/blob/abc123/")]
    public void ARepositoryGivesTheAddressOfItsFilesAtACommit(string host, string expected) =>
        Assert.Equal(expected, new GitHubRepository(host, "octo", "app").BlobUrl("abc123"));
}
