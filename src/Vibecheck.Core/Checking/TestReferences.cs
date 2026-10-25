namespace Vibecheck.Checking;

/// <summary>How a test method references a declaration: <c>direct</c> or <c>helper</c>, with the helper it calls when it is through one.</summary>
public sealed record TestReference(string Way, string? Helper = null)
{
    /// <summary>The way of a reference that one of the test method's own exercises makes.</summary>
    public const string Direct = "direct";

    /// <summary>The way of a reference that a helper the test method calls makes.</summary>
    public const string ThroughHelper = "helper";
}

/// <summary>
/// Answers which declarations of the system each test method references, and how. A test method references a
/// declaration directly when one of its own exercises names it, and through a helper when a member of a test type that
/// it calls, directly or through other such members, has an exercise that names it. Calls are followed through members
/// of test types that are not test methods, and never into the system, since following calls through the system would
/// be a static measure of coverage. See <c>docs/tests.md</c> section 5.1.
/// </summary>
public static class TestReferences
{
    /// <summary>
    /// Returns, for each test method, the declarations it references and how. A declaration both ways is direct, and one
    /// reached through several helpers names the first that the walk reaches, following calls in file and line order.
    /// </summary>
    public static Dictionary<string, Dictionary<string, TestReference>> Of(TestFacts tests)
    {
        var exercised = tests.Exercises.Where(e => e.Method is not null).ToLookup(e => e.Method!, e => e.Symbol, StringComparer.Ordinal);
        var called = tests.Calls.ToLookup(c => c.From, c => c.To, StringComparer.Ordinal);
        var methods = tests.Types.SelectMany(t => t.Methods).Select(m => m.Symbol).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, Dictionary<string, TestReference>>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var references = new Dictionary<string, TestReference>(StringComparer.Ordinal);
            foreach (var symbol in exercised[method]) references.TryAdd(symbol, new TestReference(TestReference.Direct));
            // A breadth-first walk of the helpers, each carrying the helper the test method called to reach it.
            var visited = new HashSet<string>(StringComparer.Ordinal) { method };
            var queue = new Queue<(string Member, string Helper)>();
            foreach (var helper in called[method].Where(c => !methods.Contains(c) && visited.Add(c))) queue.Enqueue((helper, helper));
            while (queue.Count > 0)
            {
                var (member, helper) = queue.Dequeue();
                foreach (var symbol in exercised[member]) references.TryAdd(symbol, new TestReference(TestReference.ThroughHelper, helper));
                foreach (var next in called[member].Where(c => !methods.Contains(c) && visited.Add(c))) queue.Enqueue((next, helper));
            }
            result[method] = references;
        }
        return result;
    }
}
