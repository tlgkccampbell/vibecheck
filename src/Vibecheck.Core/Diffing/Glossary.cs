using Vibecheck.Analysis;
using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// A concept of a system's vocabulary, as <see cref="Glossary"/> finds them: a run of words that the names of several
/// families of types share, such as "Command Handler" in <c>CreateOrderCommandHandler</c> and
/// <c>CancelOrderCommandHandler</c>. See <c>docs/attention.md</c> section 8.
/// </summary>
/// <param name="Key">The key that files the term wherever one thing per term is kept, such as Claude's definition of it, as <see cref="KeyOf"/> forms it.</param>
/// <param name="Name">The term's words, separated by spaces, as the names spell them most often, or in the singular when some name spells them so.</param>
/// <param name="Words">The term's words as <see cref="Glossary.StemOf"/> forms them, by which a name is matched against the term.</param>
/// <param name="Families">The number of families at head whose names hold the term, as <see cref="Glossary"/> defines a family.</param>
/// <param name="Instances">The types the change added or changed that the glossary reports under the term, ordered by name.</param>
/// <param name="Others">The other types at head whose names hold the term, with their leaves, ordered by name.</param>
public sealed record Term(string Key, string Name, IReadOnlyList<string> Words, int Families, List<TermInstance> Instances, List<PatternOther> Others)
{
    /// <summary>Gets a value indicating whether the term is new in the change, which is when the names at base do not make it a term.</summary>
    public bool New { get; init; }

    /// <summary>Gets the first sentence of the documentation summary of a type whose whole name is the term, or <see langword="null"/> when no such type has one.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the type whose documentation <see cref="Summary"/> comes from, or <see langword="null"/> when the term has no summary.</summary>
    public string? SummaryOf { get; init; }

    /// <summary>Returns the key of the term with some words, such as <c>term:command-handler</c>.</summary>
    public static string KeyOf(IEnumerable<string> words) => "term:" + string.Join('-', words);
}

/// <summary>A type that the change added or changed and that the glossary reports under a term.</summary>
/// <param name="Type">The type's name.</param>
/// <param name="Leaf">The leaf the type binds to at head.</param>
/// <param name="Status">How the change touched the type, as <see cref="TouchedTypes"/> names it.</param>
public sealed record TermInstance(string Type, string Leaf, string Status);

/// <summary>
/// Finds the vocabulary of a system in the names of its types, and lists the terms that a change touches, as
/// <c>docs/attention.md</c> section 8 describes. The question it answers is which concepts recur across the system's
/// names, such as "Command Handler", rather than which names a change declares.
/// </summary>
/// <remarks>
/// <para>
/// A name splits into words, as <see cref="WordsOf"/> splits it. A type's <b>family</b> is the type with the fewest
/// words whose name begins its own, so that <c>CreateOrderCommandHandler</c> belongs to the family of
/// <c>CreateOrderCommand</c>. An interface and its implementation, such as <c>IOrderStore</c> and <c>OrderStore</c>,
/// are one family.
/// </para>
/// <para>
/// A <b>term</b> is a run of up to <see cref="MaximumWords"/> words that the names of at least
/// <see cref="MinimumFamilies"/> families hold. A name that one type holds, or one type and the types named after it,
/// is therefore never a term. A run of words that name nothing on their own, such as "Base" or "Helper", is not a
/// term either.
/// </para>
/// <para>
/// A run that joins a subject to a term, such as "Order Command Handler", is left out in favor of its parts. A word is
/// a subject when more names begin with it than end with it. A shorter term is left out when every type that holds it
/// also holds a longer term that contains it, so "Handler" is listed only when some handler is neither a command
/// handler nor a query handler.
/// </para>
/// </remarks>
public static class Glossary
{
    /// <summary>The most words a term has.</summary>
    public const int MaximumWords = 3;

    /// <summary>The number of families whose names must hold a run of words for it to be a term.</summary>
    public const int MinimumFamilies = 2;

    /// <summary>The words, as <see cref="StemOf"/> forms them, that name nothing on their own. A term may hold them, but not only them.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "abstract", "add", "async", "attribute", "base", "common", "config", "core", "create", "data", "default", "delete",
        "exception", "extension", "find", "generic", "get", "helper", "impl", "implementation", "info", "internal", "item",
        "list", "load", "manager", "model", "new", "object", "remove", "save", "service", "set", "shared", "test", "type",
        "update", "util", "utility", "value", "wrapper",
    };

    /// <summary>The words, as <see cref="StemOf"/> forms them, that join other words, which neither begin nor end a term.</summary>
    private static readonly HashSet<string> Joining = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to", "with",
    };

    /// <summary>
    /// Returns the words of a type's simple name, without its namespace, containing types or type parameters. A word
    /// begins at a capital that follows a lowercase letter or a digit, or at the last capital of a run that a lowercase
    /// letter follows. An underscore or any other character that is not a letter or a digit ends a word. The
    /// <c>I</c> that begins an interface's name is dropped, so <c>IOrderRepository</c> has the words "Order" and
    /// "Repository".
    /// </summary>
    public static List<string> WordsOf(string typeName)
    {
        var name = TypeNames.Plain(typeName);
        var cut = name.IndexOfAny(['<', '`']);
        if (cut >= 0) name = name[..cut];
        name = name[(name.LastIndexOfAny(['.', '+']) + 1)..];

        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= name.Length; i++)
        {
            var c = i < name.Length ? name[i] : '\0';
            if (!char.IsLetterOrDigit(c))
            {
                if (start >= 0) words.Add(name[start..i]);
                start = -1;
                continue;
            }
            var begins = start >= 0 && char.IsUpper(c)
                && (!char.IsUpper(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]));
            if (begins) words.Add(name[start..i]);
            if (start < 0 || begins) start = i;
        }
        if (words.Count > 1 && words[0] == "I") words.RemoveAt(0);
        return words;
    }

    /// <summary>
    /// Returns the form of a word by which the glossary matches it: the word in lower case, made singular when it ends
    /// in a plain plural, so that "Changes" and "Change" are one word. A word of three letters or fewer, and one that
    /// ends in "ss", "us" or "is", is left as it is.
    /// </summary>
    public static string StemOf(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.Length <= 3 || !w.EndsWith('s') || w.EndsWith("ss", StringComparison.Ordinal) || w.EndsWith("us", StringComparison.Ordinal) || w.EndsWith("is", StringComparison.Ordinal)) return w;
        return w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4 ? w[..^3] + "y" : w[..^1];
    }

    /// <summary>One term found among the names of some types.</summary>
    /// <param name="Words">The term's words as <see cref="StemOf"/> forms them.</param>
    /// <param name="Name">The term's words as <see cref="Term.Name"/> spells them.</param>
    /// <param name="Types">The names of the types that hold the term, ordered by name.</param>
    /// <param name="Families">The number of families whose names hold the term.</param>
    public sealed record Found(IReadOnlyList<string> Words, string Name, IReadOnlyList<string> Types, int Families);

    /// <summary>Returns the terms among the names of some types, ordered by name, by the rules that <see cref="Glossary"/> sets out.</summary>
    public static List<Found> Find(IEnumerable<TypeFact> types)
    {
        var named = types.Select(t => (t.Name, Words: WordsOf(t.Name))).Where(t => t.Words.Count > 0)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var stems = named.ToDictionary(t => t.Name, t => t.Words.Select(StemOf).ToList(), StringComparer.Ordinal);

        // A family is named by its first type with the fewest words, and each type joins the family of the shortest
        // name that begins its own.
        var byWords = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _) in named) byWords.TryAdd(string.Join(' ', stems[name]), name);
        var familyOf = named.ToDictionary(t => t.Name, t =>
        {
            var words = stems[t.Name];
            return Enumerable.Range(1, words.Count).Select(n => byWords.GetValueOrDefault(string.Join(' ', words.Take(n)))).First(f => f is not null)!;
        }, StringComparer.Ordinal);

        // How many names of more than one word begin and end with each word, which tells a subject from a role.
        var begins = new Dictionary<string, int>(StringComparer.Ordinal);
        var ends = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var words in stems.Values.Where(w => w.Count > 1))
        {
            begins[words[0]] = begins.GetValueOrDefault(words[0]) + 1;
            ends[words[^1]] = ends.GetValueOrDefault(words[^1]) + 1;
        }

        var runs = new Dictionary<string, (List<string> Words, SortedSet<string> Types, HashSet<string> Families, Dictionary<string, int> Spellings)>(StringComparer.Ordinal);
        foreach (var (name, original) in named)
        {
            var words = stems[name];
            for (var i = 0; i < words.Count; i++)
            {
                for (var n = 1; n <= MaximumWords && i + n <= words.Count; n++)
                {
                    var key = string.Join(' ', words.Skip(i).Take(n));
                    if (!runs.TryGetValue(key, out var run)) runs[key] = run = (words.Skip(i).Take(n).ToList(), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                    run.Types.Add(name);
                    run.Families.Add(familyOf[name]);
                    var spelled = string.Join(' ', original.Skip(i).Take(n));
                    run.Spellings[spelled] = run.Spellings.GetValueOrDefault(spelled) + 1;
                }
            }
        }

        bool Names(string w) => w.Length >= 3 && w.Any(char.IsLetter) && !Generic.Contains(w);
        bool Qualifies(List<string> words) => words.Any(Names) && !Joining.Contains(words[0]) && !Joining.Contains(words[^1]);
        var candidates = runs.Values.Where(r => r.Families.Count >= MinimumFamilies && Qualifies(r.Words))
            .ToDictionary(r => string.Join(' ', r.Words), StringComparer.Ordinal);
        bool Subject(string w) => begins.GetValueOrDefault(w) > ends.GetValueOrDefault(w);
        var joined = candidates.Where(c => c.Value.Words.Count > 1 && Subject(c.Value.Words[0]) && candidates.ContainsKey(string.Join(' ', c.Value.Words.Skip(1))))
            .Select(c => c.Key).ToList();
        foreach (var key in joined) candidates.Remove(key);

        // The longest terms are kept first, so that a shorter one can be left out when they cover every type that holds it.
        var keptOf = new Dictionary<string, List<List<string>>>(StringComparer.Ordinal);
        var kept = new List<Found>();
        foreach (var r in candidates.Values.OrderByDescending(r => r.Words.Count).ThenBy(r => string.Join(' ', r.Words), StringComparer.Ordinal))
        {
            if (r.Types.All(t => keptOf.TryGetValue(t, out var longer) && longer.Any(l => l.Count > r.Words.Count && Contains(l, r.Words)))) continue;
            foreach (var t in r.Types)
            {
                if (!keptOf.TryGetValue(t, out var list)) keptOf[t] = list = [];
                list.Add(r.Words);
            }
            var stem = string.Join(' ', r.Words);
            var spelling = r.Spellings.OrderBy(s => s.Key.Equals(stem, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key;
            kept.Add(new Found(r.Words, spelling, r.Types.ToList(), r.Families.Count));
        }
        return kept.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the terms that a change touches, which are those under which it reports a type it added or changed. A
    /// type is reported under each term its name holds, except a term that is part of a longer one the name also
    /// holds, so that <c>CreateOrderCommandHandler</c> is reported under "Command Handler" and not under "Handler". New
    /// terms come first, then those with the most reported types.
    /// </summary>
    /// <param name="baseFacts">The facts at base, whose terms tell which terms are new.</param>
    /// <param name="headFacts">The facts at head, whose types the terms are found among.</param>
    /// <param name="files">Every file of the changeset, with its status.</param>
    public static List<Term> Build(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files)
    {
        static List<TypeFact> CandidatesOf(Facts facts) =>
            PatternDiscovery.Candidates(facts.Types, facts.Files.Where(f => f.Generator is not null).Select(f => f.Path).ToHashSet(StringComparer.Ordinal));
        var types = CandidatesOf(headFacts);
        var found = Find(types);
        var atBase = Find(CandidatesOf(baseFacts)).Select(f => string.Join(' ', f.Words)).ToHashSet(StringComparer.Ordinal);
        var statusOf = TouchedTypes.StatusOf(baseFacts, files);
        var typeOf = types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var held = new Dictionary<string, List<Found>>(StringComparer.Ordinal);
        foreach (var f in found)
            foreach (var t in f.Types)
            {
                if (!held.TryGetValue(t, out var list)) held[t] = list = [];
                list.Add(f);
            }
        var reported = new Dictionary<Found, List<TermInstance>>();
        foreach (var (name, terms) in held)
        {
            if (statusOf(typeOf[name]) is not { } status) continue;
            foreach (var f in terms.Where(f => !terms.Any(o => o.Words.Count > f.Words.Count && Contains(o.Words, f.Words))))
            {
                if (!reported.TryGetValue(f, out var list)) reported[f] = list = [];
                list.Add(new TermInstance(name, typeOf[name].Leaf, status));
            }
        }

        var summaries = headFacts.Declarations.Where(d => d.Kind == "type" && d.Summary is not null)
            .GroupBy(d => d.Symbol, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Summary!, StringComparer.Ordinal);
        return reported.Select(r =>
            {
                var (f, instances) = (r.Key, r.Value.OrderBy(i => i.Type, StringComparer.Ordinal).ToList());
                var mine = instances.Select(i => i.Type).ToHashSet(StringComparer.Ordinal);
                // A type whose whole name is the term, such as IConsumer for "Consumer", documents it.
                var anchor = f.Types.FirstOrDefault(t => summaries.ContainsKey(TypeNames.Plain(t)) && WordsOf(t).Select(StemOf).SequenceEqual(f.Words));
                return new Term(Term.KeyOf(f.Words), f.Name, f.Words, f.Families, instances,
                    f.Types.Where(t => !mine.Contains(t)).Select(t => new PatternOther(t, typeOf[t].Leaf)).ToList())
                {
                    New = !atBase.Contains(string.Join(' ', f.Words)),
                    Summary = anchor is null ? null : summaries[TypeNames.Plain(anchor)],
                    SummaryOf = anchor,
                };
            })
            .OrderByDescending(t => t.New).ThenByDescending(t => t.Instances.Count).ThenByDescending(t => t.Families)
            .ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Returns a value indicating whether a run of words holds another run, in order and adjacent.</summary>
    private static bool Contains(IReadOnlyList<string> words, IReadOnlyList<string> part)
    {
        for (var i = 0; i + part.Count <= words.Count; i++)
            if (part.Select((w, j) => (w, j)).All(x => words[i + x.j] == x.w)) return true;
        return false;
    }
}
