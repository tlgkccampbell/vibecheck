using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Archmap.Analysis;

/// <summary>
/// Walks one file's syntax tree and records every symbol reference that resolves to
/// another file, an external assembly, or a generated declaration. Namespace imports,
/// references within the same file, and references to the core library are not
/// recorded.
/// </summary>
internal sealed class DependencyWalker : CSharpSyntaxWalker
{
    private static readonly SymbolDisplayFormat DisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    private readonly SemanticModel model;
    private readonly SourceResolver resolver;
    private readonly string currentFile;
    private readonly IAssemblySymbol coreLibrary;

    public List<RawDependency> Dependencies { get; } = [];
    public List<ExternalReference> ExternalReferences { get; } = [];
    public List<UnresolvableReference> Unresolvable { get; } = [];

    public DependencyWalker(SemanticModel model, Compilation compilation, SourceResolver resolver, string currentFile)
    {
        this.model = model;
        this.resolver = resolver;
        this.currentFile = currentFile;
        coreLibrary = compilation.ObjectType.ContainingAssembly;
    }

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

        var target = symbol;
        if (target.DeclaringSyntaxReferences.Length == 0 && target.ContainingType is not null)
        {
            target = target.ContainingType;
        }
        var typeName = (symbol as INamedTypeSymbol ?? symbol.ContainingType)?.Name ?? symbol.Name;
        var display = symbol.ToDisplayString(DisplayFormat);
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        if (target.DeclaringSyntaxReferences.Length == 0)
        {
            var assembly = symbol.ContainingAssembly;
            if (assembly is null || SymbolEqualityComparer.Default.Equals(assembly, coreLibrary)) return;
            ExternalReferences.Add(new ExternalReference(currentFile, assembly.Name, display, line));
            return;
        }

        // A symbol declared in several places, such as a partial type or a partial
        // method with a generated implementation, resolves to its first compiled source
        // declaration. A symbol declared only in generated code is unresolvable.
        var paths = target.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).ToList();
        var file = paths.Select(resolver.SourceOf).Where(p => p is not null).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        if (file is null)
        {
            Unresolvable.Add(new UnresolvableReference(currentFile, display, resolver.ReasonFor(paths.FirstOrDefault()), line));
            return;
        }
        if (file == currentFile) return;
        Dependencies.Add(new RawDependency(currentFile, file, KindOf(node, symbol), display, typeName, line));
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
