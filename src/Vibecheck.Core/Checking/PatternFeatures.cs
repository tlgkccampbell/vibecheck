using Vibecheck.Analysis;
using Vibecheck.Model;

namespace Vibecheck.Checking;

/// <summary>
/// Gives the features of the instances of patterns at one end of a change, and the norms of patterns. A feature is a
/// fact about an instance that its pattern's instances can share: a trait other than the pattern's keys, an injected
/// type, a use, being tested, being documented, or having a companion in another pattern. A norm is a feature that at
/// least <see cref="NormShare"/> of a pattern's instances share, in a pattern of at least <see cref="NormMinimum"/>
/// instances. See <c>docs/patterns.md</c> section 5.
/// </summary>
public sealed class PatternFeatures
{
    /// <summary>The fewest instances a pattern has for any feature to be a norm of it.</summary>
    public const int NormMinimum = 5;

    /// <summary>The share of a pattern's instances, as a fraction, that a feature must have to be a norm.</summary>
    public const double NormShare = 0.8;

    /// <summary>The feature of an instance that is the subject of a test type.</summary>
    public const string Tested = "tested";

    /// <summary>The feature of an instance whose type has a documentation summary.</summary>
    public const string Documented = "documented";

    /// <summary>The prefix of the feature of an instance that injects a type.</summary>
    public const string Injects = "injects:";

    /// <summary>The prefix of the feature of an instance that uses a type.</summary>
    public const string Uses = "uses:";

    /// <summary>The prefix of the feature of an instance that has a companion in another pattern, which the identifier of that pattern follows.</summary>
    public const string Companion = "companion:";

    private readonly IReadOnlyList<PatternFact> patterns;
    private readonly Dictionary<string, SortedSet<string>> uses = new(StringComparer.Ordinal);
    private readonly HashSet<string> tested;
    private readonly HashSet<string> documented;
    private readonly HashSet<string> system;
    // For each pattern, the first instance, by name, that has each key type argument.
    private readonly Dictionary<string, Dictionary<string, string>> byArgument = new(StringComparer.Ordinal);

    /// <summary>Initializes the features of one end of a change.</summary>
    /// <param name="facts">The facts of the end.</param>
    /// <param name="patterns">The patterns, which are found at base whichever end this is.</param>
    /// <param name="instances">The instances of each pattern at this end, by the pattern's identifier.</param>
    public PatternFeatures(Facts facts, IReadOnlyList<PatternFact> patterns, IReadOnlyDictionary<string, List<TypeFact>> instances)
    {
        this.patterns = patterns;
        system = facts.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (from, to) in facts.Dependencies.Select(d => (d.From, d.To)).Concat(facts.ExternalRefs.Select(e => (e.From, e.Type))))
        {
            if (!uses.TryGetValue(from, out var set)) uses[from] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(to);
        }
        tested = facts.Tests.Types.Select(t => t.Subject?.Type).OfType<string>().ToHashSet(StringComparer.Ordinal);
        documented = facts.Declarations.Where(d => d.Kind == "type" && d.Container is null && d.Summary is not null).Select(d => d.Type).ToHashSet(StringComparer.Ordinal);
        foreach (var p in patterns)
        {
            var first = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in instances.GetValueOrDefault(p.Id, []).OrderBy(t => t.Name, StringComparer.Ordinal))
                foreach (var argument in KeyArguments(t, p)) first.TryAdd(argument, t.Name);
            byArgument[p.Id] = first;
        }
    }

    /// <summary>Returns the features of a type as an instance of a pattern.</summary>
    public SortedSet<string> Of(TypeFact type, PatternFact pattern)
    {
        var features = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in PatternDiscovery.KeysOf(type.Traits).Where(k => !pattern.Keys.Contains(k))) features.Add(key);
        var injects = type.Traits?.Injects ?? [];
        foreach (var t in injects) features.Add(Injects + t);
        // A use of a type the instance already carries or injects says nothing more, so it is not a feature of its own.
        var said = PatternDiscovery.KeysOf(type.Traits).Select(PatternKeys.ValueOf).Concat(injects).Select(Bare).ToHashSet(StringComparer.Ordinal);
        foreach (var u in uses.GetValueOrDefault(type.Name) ?? []) if (!said.Contains(Bare(u))) features.Add(Uses + u);
        if (tested.Contains(type.Name)) features.Add(Tested);
        if (documented.Contains(type.Name)) features.Add(Documented);
        foreach (var other in patterns.Where(p => p.Id != pattern.Id))
            if (CompanionOf(type, pattern, other.Id) is not null) features.Add(Companion + other.Id);
        return features;
    }

    /// <summary>
    /// Returns the instance of another pattern that is a type's companion, or <see langword="null"/>. Two instances are
    /// companions when a key type argument of one is a key type argument of the other. Only a type of the system links
    /// instances, so the types of the core library, such as <c>string</c> or <c>Guid</c>, never do.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="pattern">The pattern the type is an instance of, whose keys give its key type arguments.</param>
    /// <param name="other">The identifier of the other pattern.</param>
    public string? CompanionOf(TypeFact type, PatternFact pattern, string other)
    {
        if (!byArgument.TryGetValue(other, out var first)) return null;
        return KeyArguments(type, pattern).Select(a => first.GetValueOrDefault(a)).FirstOrDefault(n => n is not null && n != type.Name);
    }

    /// <summary>Returns the type arguments that the traits of a type give the keys of a pattern, keeping those that are types of the system.</summary>
    public IEnumerable<string> KeyArguments(TypeFact type, PatternFact pattern)
    {
        if (type.Traits is not { } traits) yield break;
        var bases = traits.Base is null ? [] : new[] { (PatternKeys.Inherits, traits.Base) };
        foreach (var (kind, trait) in bases.Concat(traits.Interfaces.Select(i => (PatternKeys.Implements, i))))
        {
            if (!pattern.Keys.Contains(PatternKeys.Of(kind, trait.Type))) continue;
            foreach (var argument in trait.Arguments ?? []) if (system.Contains(argument)) yield return argument;
        }
    }

    /// <summary>
    /// Returns the norms of a pattern among the features of its instances, most shared first, or none when the pattern
    /// has fewer than <see cref="NormMinimum"/> instances.
    /// </summary>
    public static List<NormFact> Norms(IReadOnlyList<SortedSet<string>> features)
    {
        if (features.Count < NormMinimum) return [];
        return features.SelectMany(f => f).GroupBy(f => f, StringComparer.Ordinal)
            .Select(g => new NormFact(g.Key, g.Count()))
            .Where(n => n.Share >= NormShare * features.Count)
            .OrderByDescending(n => n.Share).ThenBy(n => n.Feature, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the patterns of a commit's facts with their norms, and the mean number of norms their own instances lack,
    /// which says whether the thresholds suit the pattern.
    /// </summary>
    public static List<PatternFact> WithNorms(Facts facts)
    {
        var types = facts.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var instances = facts.Patterns.ToDictionary(p => p.Id, p => p.Instances.Select(n => types[n]).ToList(), StringComparer.Ordinal);
        var features = new PatternFeatures(facts, facts.Patterns, instances);
        return facts.Patterns.Select(p =>
        {
            var each = instances[p.Id].Select(t => features.Of(t, p)).ToList();
            var norms = Norms(each);
            var lacking = norms.Count == 0 ? 0 : each.Average(f => norms.Count(n => !f.Contains(n.Feature)));
            return p with { Norms = norms, MeanDepartures = Math.Round(lacking, 2) };
        }).ToList();
    }

    /// <summary>Returns the words that say an instance has a feature, such as "injects IUnitOfWork".</summary>
    /// <param name="feature">The feature.</param>
    /// <param name="nameOf">Returns the name of a pattern from its identifier, which names a companion pattern.</param>
    public static string Text(string feature, Func<string, string> nameOf) => feature switch
    {
        Tested => "is tested",
        Documented => "is documented",
        _ when feature.StartsWith(Injects, StringComparison.Ordinal) => "injects " + ShortName(feature[Injects.Length..]),
        _ when feature.StartsWith(Uses, StringComparison.Ordinal) => "uses " + ShortName(feature[Uses.Length..]),
        _ when feature.StartsWith(Companion, StringComparison.Ordinal) => "has a companion " + nameOf(feature[Companion.Length..]),
        _ when PatternKeys.KindOf(feature) == PatternKeys.Attribute => $"carries [{AttributeName(PatternKeys.ValueOf(feature))}]",
        _ => PatternKeys.KindOf(feature) + " " + ShortName(PatternKeys.ValueOf(feature)),
    };

    /// <summary>Returns the words that say an instance lacks a feature, such as "no IUnitOfWork" or "no test".</summary>
    /// <param name="feature">The feature.</param>
    /// <param name="nameOf">Returns the name of a pattern from its identifier, which names a companion pattern.</param>
    public static string Absence(string feature, Func<string, string> nameOf) => feature switch
    {
        Tested => "no test",
        Documented => "no summary",
        _ when feature.StartsWith(Injects, StringComparison.Ordinal) => "no " + ShortName(feature[Injects.Length..]),
        _ when feature.StartsWith(Uses, StringComparison.Ordinal) => "no use of " + ShortName(feature[Uses.Length..]),
        _ when feature.StartsWith(Companion, StringComparison.Ordinal) => "no companion " + nameOf(feature[Companion.Length..]),
        _ when PatternKeys.KindOf(feature) == PatternKeys.Attribute => $"no [{AttributeName(PatternKeys.ValueOf(feature))}]",
        _ => $"does not {(PatternKeys.KindOf(feature) == PatternKeys.Inherits ? "inherit" : "implement")} {ShortName(PatternKeys.ValueOf(feature))}",
    };

    /// <summary>Returns the last segment of a type's open name, such as <c>ILogger&lt;&gt;</c>.</summary>
    private static string ShortName(string type)
    {
        var bare = Bare(type);
        return type[(bare.LastIndexOf('.') + 1)..];
    }

    private static string AttributeName(string type)
    {
        var name = ShortName(type);
        return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length ? name[..^"Attribute".Length] : name;
    }

    /// <summary>Returns a type's name without its type parameters or arguments, so that an open name and a plain one compare.</summary>
    private static string Bare(string type) => type.IndexOf('<') is var i and >= 0 ? type[..i] : type;
}
