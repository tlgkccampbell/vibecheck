using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vibecheck.Analysis;

/// <summary>
/// The symbol a name denotes, as <see cref="SemanticModel.GetSymbolInfo(SyntaxNode, CancellationToken)"/> reports it:
/// the symbol when binding succeeded, and otherwise the first candidate, such as the method an overload resolution
/// failure left behind.
/// </summary>
internal readonly record struct BoundName(ISymbol? Symbol, ISymbol? Candidate)
{
    public BoundName(SymbolInfo info) : this(info.Symbol, info.CandidateSymbols.FirstOrDefault()) { }

    /// <summary>Gets the symbol, or the first candidate when there is no symbol.</summary>
    public ISymbol? SymbolOrCandidate => Symbol ?? Candidate;
}

/// <summary>
/// Answers which symbol a simple name in one syntax tree denotes, as the semantic model would, without binding the
/// member that holds the name more than once. The semantic model caches the bound nodes of a member body, but a name
/// that has no bound node of its own, such as a namespace qualifier, the type in an object creation, or the method
/// name of a call, misses that cache, and each miss inside a field or property initializer binds the whole initializer
/// again. A generated table of a few hundred rows written with <c>global::</c> qualifiers costs minutes that way. This
/// binder resolves such names through the binder of the enclosing scope instead, standalone, and asks the semantic
/// model only for names whose bound nodes it keeps.
/// </summary>
internal sealed class NameBinder(SemanticModel model)
{
    private readonly Dictionary<SyntaxNode, BoundName> names = [];
    private readonly Dictionary<SyntaxNode, BoundName> receivers = [];
    private readonly Dictionary<(int Anchor, string Text), BoundName> qualifiers = [];

    /// <summary>Returns the symbol a simple name denotes. A name asked for twice is resolved once.</summary>
    public BoundName Bind(SimpleNameSyntax name)
    {
        if (names.TryGetValue(name, out var known)) return known;
        var bound = Resolve(name);
        names[name] = bound;
        return bound;
    }

    private BoundName Resolve(SimpleNameSyntax name)
    {
        // The type behind var, and the attribute suffix rule, are known only to binding in place.
        if (name.IsVar || IsAttributeName(name)) return InPlace(name);
        switch (name.Parent)
        {
            case AliasQualifiedNameSyntax alias when alias.Alias == name:
                // An alias such as global:: names a namespace, which is all the walker asks of it.
                return new BoundName(model.Compilation.GlobalNamespace, null);
            case AliasQualifiedNameSyntax alias:
                return Qualifier(alias);
            case QualifiedNameSyntax qualified when qualified.Right == name:
                return Qualifier(qualified);
            case QualifiedNameSyntax:
                return Qualifier(name);
            case MemberAccessExpressionSyntax access when access.Expression == name:
                return Receiver(name);
            case MemberAccessExpressionSyntax access when access.Name == name:
                if (Receiver(access.Expression).Symbol is INamespaceSymbol) return Qualifier(access);
                return access.Parent is InvocationExpressionSyntax call && call.Expression == access ? Invoked(call, name) : InPlace(name);
            case InvocationExpressionSyntax invocation when invocation.Expression == name:
                return Invoked(invocation, name);
            default:
                return SyntaxFacts.IsInNamespaceOrTypeContext(name) || SyntaxFacts.IsInTypeOnlyContext(name) ? Qualifier(name) : InPlace(name);
        }
    }

    /// <summary>
    /// Returns the symbol of a member access receiver, remembered per node. A receiver that is a simple name is bound
    /// where it stands, since a local or a parameter may shadow a type or namespace there. A member access whose own
    /// receiver is a namespace names a namespace or a type, so it is bound as one.
    /// </summary>
    private BoundName Receiver(ExpressionSyntax expression)
    {
        if (receivers.TryGetValue(expression, out var known)) return known;
        var bound = expression switch
        {
            SimpleNameSyntax => new BoundName(model.GetSpeculativeSymbolInfo(expression.SpanStart, expression, SpeculativeBindingOption.BindAsExpression)),
            AliasQualifiedNameSyntax alias => Qualifier(alias),
            MemberAccessExpressionSyntax inner => Receiver(inner.Expression).Symbol is INamespaceSymbol ? Qualifier(inner) : InPlace(inner),
            _ => InPlace(expression),
        };
        receivers[expression] = bound;
        return bound;
    }

    /// <summary>
    /// Returns the method a call invokes, read from the call, which has a bound node, rather than from its name, which
    /// has none. A call through a delegate names the field or property that holds the delegate, so that case, and a
    /// call that resolves to nothing, bind the name itself.
    /// </summary>
    private BoundName Invoked(InvocationExpressionSyntax call, SimpleNameSyntax name)
    {
        var bound = InPlace(call);
        return bound.Symbol is null or IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } ? InPlace(name) : bound;
    }

    /// <summary>
    /// Binds a namespace or type name, which may be written as a member access, standalone at the anchor of its
    /// member, and remembers the answer by anchor and text. A name that does not parse as a type is bound standalone
    /// where it stands.
    /// </summary>
    private BoundName Qualifier(ExpressionSyntax node)
    {
        var anchor = AnchorOf(node);
        var text = node.ToString();
        if (qualifiers.TryGetValue((anchor, text), out var known)) return known;
        var parsed = SyntaxFactory.ParseTypeName(text);
        var bound = parsed.ContainsDiagnostics
            ? new BoundName(model.GetSpeculativeSymbolInfo(node.SpanStart, node, node is TypeSyntax ? SpeculativeBindingOption.BindAsTypeOrNamespace : SpeculativeBindingOption.BindAsExpression))
            : new BoundName(model.GetSpeculativeSymbolInfo(anchor, parsed, SpeculativeBindingOption.BindAsTypeOrNamespace));
        qualifiers[(anchor, text)] = bound;
        return bound;
    }

    private BoundName InPlace(SyntaxNode node) => new(model.GetSymbolInfo(node));

    /// <summary>
    /// Returns the position a qualified name inside a member is bound at: the first token after the member's
    /// modifiers, where the member's own type parameters are in scope and no member body is. A name outside any member,
    /// or in a type declaration's own header, is bound where it stands.
    /// </summary>
    private static int AnchorOf(SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax);
        if (member is null) return node.SpanStart;
        var afterModifiers = member.Modifiers.Count > 0 ? member.Modifiers.Last().Span.End : member.AttributeLists.Count > 0 ? member.AttributeLists.Last().Span.End : member.SpanStart;
        var token = member.FindToken(afterModifiers);
        if (token.Span.End <= afterModifiers) token = token.GetNextToken();
        return token.SpanStart;
    }

    /// <summary>Returns a value indicating whether a name is, or qualifies, the name of an attribute.</summary>
    private static bool IsAttributeName(SimpleNameSyntax name) =>
        name.Ancestors().TakeWhile(a => a is NameSyntax or AttributeSyntax).Any(a => a is AttributeSyntax);
}
