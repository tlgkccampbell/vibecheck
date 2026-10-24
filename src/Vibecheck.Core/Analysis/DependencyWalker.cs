using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vibecheck.Analysis;

/// <summary>
/// Walks one document's syntax tree and records every symbol reference as a
/// dependency from the top-level type that contains the reference to the top-level
/// type that declares the symbol, or to an external assembly, or as unresolvable.
/// Namespace imports, references within one type, references to the core library,
/// and references to the <c>[Vibecheck]</c> attribute itself are not recorded.
/// </summary>
internal sealed class DependencyWalker : CSharpSyntaxWalker
{
    private static readonly SymbolDisplayFormat SymbolFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    private readonly SemanticModel model;
    private readonly SourceResolver resolver;
    private readonly string file;
    private readonly string assembly;
    private readonly IAssemblySymbol coreLibrary;

    /// <summary>A dependency before duplicate names are qualified: the plain type names and the assemblies that declare them.</summary>
    internal sealed record Walked(string FromType, string ToType, string ToAssembly, string Kind, string Symbol, int Line);

    public List<Walked> Dependencies { get; } = [];
    public List<ExternalReference> ExternalReferences { get; } = [];
    public List<UnresolvableReference> Unresolvable { get; } = [];

    public DependencyWalker(SemanticModel model, Compilation compilation, SourceResolver resolver, string file)
    {
        this.model = model;
        this.resolver = resolver;
        this.file = file;
        assembly = compilation.AssemblyName ?? "";
        coreLibrary = compilation.ObjectType.ContainingAssembly;
    }

    /// <summary>The assembly of the document being walked, which is the assembly of every source type in it.</summary>
    public string Assembly => assembly;

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        // A namespace import creates no edge, and a using alias is resolved at its uses.
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        Record(node);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        Record(node);
        base.VisitGenericName(node);
    }

    private void Record(SimpleNameSyntax node)
    {
        if (IsNamespaceName(node)) return;
        if (node.Parent is MemberAccessExpressionSyntax { Expression: var left } && left == node)
        {
            // The left side of `Type.Member` is covered by the member reference itself.
            var leftSymbol = model.GetSymbolInfo(node).Symbol;
            if (leftSymbol is INamedTypeSymbol or INamespaceSymbol) return;
        }

        var from = EnclosingTopLevelType(node);
        if (from is null) return;

        var info = model.GetSymbolInfo(node);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is null) return;
        switch (symbol.Kind)
        {
            case SymbolKind.Namespace:
            case SymbolKind.Local:
            case SymbolKind.Parameter:
            case SymbolKind.TypeParameter:
            case SymbolKind.Label:
            case SymbolKind.RangeVariable:
            case SymbolKind.Preprocessing:
            case SymbolKind.Discard:
            case SymbolKind.DynamicType:
            case SymbolKind.ErrorType:
                return;
        }
        if (symbol is IMethodSymbol { ReducedFrom: not null } reduced) symbol = reduced.ReducedFrom!;
        symbol = symbol.OriginalDefinition;
        if (symbol is IArrayTypeSymbol array) symbol = array.ElementType.OriginalDefinition;
        if (symbol is IPointerTypeSymbol) return;

        var target = TopLevelTypeOf(symbol);
        if (target is null || target.IsAnonymousType) return;
        var display = symbol.ToDisplayString(SymbolFormat);
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var fromName = from.ToDisplayString(RoslynAnalyzer.TypeNameFormat);

        if (target.DeclaringSyntaxReferences.Length == 0)
        {
            var assembly = target.ContainingAssembly;
            if (assembly is null || SymbolEqualityComparer.Default.Equals(assembly, coreLibrary)) return;
            ExternalReferences.Add(new ExternalReference(fromName, assembly.Name, display, file, line));
            return;
        }

        var targetName = target.ToDisplayString(RoslynAnalyzer.TypeNameFormat);
        if (targetName == RoslynAnalyzer.AttributeName) return;
        if (SymbolEqualityComparer.Default.Equals(target, from)) return;
        var paths = target.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).ToList();
        if (!paths.Any(p => resolver.SourceOf(p) is not null))
        {
            Unresolvable.Add(new UnresolvableReference(fromName, display, resolver.ReasonFor(paths.FirstOrDefault()), file, line));
            return;
        }
        Dependencies.Add(new Walked(fromName, targetName, target.ContainingAssembly?.Name ?? "", KindOf(node, symbol), display, line));
    }

    /// <summary>Returns the top-level type whose declaration contains a node, or <see langword="null"/> outside any type, as in an assembly attribute.</summary>
    private INamedTypeSymbol? EnclosingTopLevelType(SyntaxNode node)
    {
        var enclosing = model.GetEnclosingSymbol(node.SpanStart);
        return TopLevelTypeOf(enclosing);
    }

    /// <summary>Returns the top-level type that declares a symbol: the symbol itself when it is a type, else its containing type, lifted out of any nesting.</summary>
    private static INamedTypeSymbol? TopLevelTypeOf(ISymbol? symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        if (type is null) return null;
        type = type.OriginalDefinition;
        while (type.ContainingType is not null) type = type.ContainingType.OriginalDefinition;
        return type;
    }

    private static bool IsNamespaceName(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is BaseNamespaceDeclarationSyntax ns && ns.Name.Span.Contains(node.Span)) return true;
            if (n is not (QualifiedNameSyntax or AliasQualifiedNameSyntax)) return false;
        }
        return false;
    }

    private static string KindOf(SimpleNameSyntax node, ISymbol symbol)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case AttributeSyntax:
                    return "attribute";
                case BaseTypeSyntax:
                    return symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } ? "implements" : "inherits";
                case ObjectCreationExpressionSyntax creation when creation.Type.Span.Contains(node.Span):
                    return "constructs";
                case QualifiedNameSyntax:
                case AliasQualifiedNameSyntax:
                case MemberAccessExpressionSyntax:
                case NullableTypeSyntax:
                case ArrayTypeSyntax:
                case TypeArgumentListSyntax:
                case GenericNameSyntax:
                    continue;
                default:
                    if (symbol is IMethodSymbol) return "calls";
                    return "references";
            }
        }
        return symbol is IMethodSymbol ? "calls" : "references";
    }
}
