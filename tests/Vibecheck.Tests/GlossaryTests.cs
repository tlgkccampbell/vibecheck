using Vibecheck.Checking;
using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the glossary: the concepts that recur across the names of a system's types, and those a change touches. See <c>docs/attention.md</c> section 8.</summary>
public class GlossaryTests
{
    [Theory]
    [InlineData("A.B.IOrderRepository", "Order Repository")]
    [InlineData("A.HTTPClient", "HTTP Client")]
    [InlineData("A.Base64Encoder", "Base64 Encoder")]
    [InlineData("A.Result<T>", "Result")]
    [InlineData("Lib::A.IO", "IO")]
    [InlineData("A.Order_Line", "Order Line")]
    [InlineData("A.I", "I")]
    public void SplitsANameIntoWords(string name, string words) =>
        Assert.Equal(words, string.Join(' ', Glossary.WordsOf(name)));

    [Theory]
    [InlineData("Changes", "change")]
    [InlineData("Policies", "policy")]
    [InlineData("Status", "status")]
    [InlineData("Class", "class")]
    [InlineData("Analysis", "analysis")]
    [InlineData("Ids", "ids")]
    public void MatchesAPlainPluralAsItsSingular(string word, string stem) => Assert.Equal(stem, Glossary.StemOf(word));

    [Fact]
    public void OneTermHoldsTheSingularAndThePluralAndIsSpelledInTheSingular()
    {
        var found = Assert.Single(Glossary.Find(new[] { "FileHunks", "GitHunks", "Hunk" }.Select(Type)));

        Assert.Equal(("Hunk", 3), (found.Name, found.Families));
    }

    private static TypeFact Type(string name) => new("A." + name, "A", "a", "pattern", [$"src/{name}.cs"]);

    /// <summary>A Mediator-style system: commands and queries about orders and invoices, each with its handler, and controllers and services beside them.</summary>
    private static readonly string[] Mediator =
    [
        "Order", "OrderController", "OrderService", "Invoice", "InvoiceController", "InvoiceService",
        "CreateOrderCommand", "CreateOrderCommandHandler", "CancelOrderCommand", "CancelOrderCommandHandler",
        "PayInvoiceCommand", "PayInvoiceCommandHandler", "GetOrderQuery", "GetOrderQueryHandler", "GetInvoiceQuery", "GetInvoiceQueryHandler",
        "Session", "ISessionReader",
    ];

    [Fact]
    public void FindsTheRunsOfWordsThatSeveralFamiliesOfNamesShare()
    {
        var found = Glossary.Find(Mediator.Select(Type));

        // "Handler" is left out, since every handler is a command or a query handler; "Order Command" and "Order Command
        // Handler" join the subject "Order" to a term; "Service" names nothing on its own; and "Session" is one type and
        // the reader named after it, which is one family.
        Assert.Equal(["Command", "Command Handler", "Controller", "Invoice", "Order", "Query", "Query Handler"], found.Select(f => f.Name));
        var handler = found.Single(f => f.Name == "Command Handler");
        Assert.Equal((3, 3), (handler.Families, handler.Types.Count));
        Assert.Equal(["command", "handler"], handler.Words);
        // The commands and their handlers both hold "Command", in the three families of the commands.
        Assert.Equal((3, 6), (found.Single(f => f.Name == "Command").Families, found.Single(f => f.Name == "Command").Types.Count));
    }

    [Fact]
    public void KeepsAShorterTermWhenSomeTypeHoldsItOutsideTheLongerOnes()
    {
        var found = Glossary.Find(Mediator.Append("RetryHandler").Append("AuditHandler").Select(Type));

        Assert.Contains(found, f => f.Name == "Handler" && f.Types.Count == 7);
    }

    private const string Consumer = "namespace A;\n\n/// <summary>Handles one message from the bus.</summary>\npublic interface IConsumer\n{\n    void Consume();\n}\n";

    private static string Class(string name, string body = "") => $"namespace A;\n\npublic class {name}\n{{\n{body}}}\n";

    [Fact]
    public void ListsTheTermsAChangeTouchesWithTheTypesItReportsUnderEach()
    {
        var before = new Dictionary<string, string>
        {
            ["src/CreateOrderCommand.cs"] = Class("CreateOrderCommand"),
            ["src/CreateOrderCommandHandler.cs"] = Class("CreateOrderCommandHandler"),
            ["src/CancelOrderCommand.cs"] = Class("CancelOrderCommand"),
            ["src/CancelOrderCommandHandler.cs"] = Class("CancelOrderCommandHandler"),
            ["src/OrderController.cs"] = Class("OrderController"),
            ["src/InvoiceController.cs"] = Class("InvoiceController"),
        };
        var after = new Dictionary<string, string>(before)
        {
            ["src/CancelOrderCommandHandler.cs"] = Class("CancelOrderCommandHandler", "    public int Handle() => 1;\n"),
            ["src/RefundOrderCommand.cs"] = Class("RefundOrderCommand"),
            ["src/RefundOrderCommandHandler.cs"] = Class("RefundOrderCommandHandler"),
            ["src/IConsumer.cs"] = Consumer,
            ["src/ReceiptConsumer.cs"] = Class("ReceiptConsumer : IConsumer", "    public void Consume() { }\n"),
            ["src/ShippingConsumer.cs"] = Class("ShippingConsumer : IConsumer", "    public void Consume() { }\n"),
        };

        var cs = Snippets.Changeset(before, after);

        // "Consumer" is new, so it comes first; "Controller" is untouched, so it is not listed.
        Assert.Equal(["Consumer", "Order", "Command Handler", "Command"], cs.Glossary.Select(t => t.Name));
        var consumer = cs.Glossary[0];
        Assert.True(consumer.New);
        Assert.Equal(("term:consumer", "Handles one message from the bus.", "A.IConsumer"), (consumer.Key, consumer.Summary, consumer.SummaryOf));
        Assert.Equal(["A.IConsumer", "A.ReceiptConsumer", "A.ShippingConsumer"], consumer.Instances.Select(i => i.Type));

        var handler = cs.Glossary[2];
        Assert.False(handler.New);
        Assert.Null(handler.Summary);
        Assert.Equal([("A.CancelOrderCommandHandler", "changed"), ("A.RefundOrderCommandHandler", "added")], handler.Instances.Select(i => (i.Type, i.Status)));
        Assert.Equal(["A.CreateOrderCommandHandler"], handler.Others.Select(o => o.Type));
        Assert.Equal(3, handler.Families);
        // A handler holds "Command" within "Command Handler", so it is reported under the longer term only.
        Assert.Equal(["A.RefundOrderCommand"], cs.Glossary[3].Instances.Select(i => i.Type));
        Assert.Equal(["A.CancelOrderCommandHandler", "A.RefundOrderCommand", "A.RefundOrderCommandHandler"], cs.Glossary[1].Instances.Select(i => i.Type));
    }

    [Fact]
    public void TheGlossaryRoundTripsThroughJson()
    {
        var before = new Dictionary<string, string> { ["src/AConsumer.cs"] = Class("AConsumer") };
        var cs = Snippets.Changeset(before, new Dictionary<string, string>(before) { ["src/BConsumer.cs"] = Class("BConsumer") });
        var back = FactsJson.Deserialize<Changeset>(FactsJson.Serialize(cs));

        Assert.Single(cs.Glossary);
        Assert.Equal(FactsJson.Serialize(cs.Glossary), FactsJson.Serialize(back.Glossary));
    }
}
