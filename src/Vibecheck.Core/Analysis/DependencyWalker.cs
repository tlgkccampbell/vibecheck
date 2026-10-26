using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vibecheck.Analysis;

/// <summary>
/// Walks one document's syntax tree and records every symbol reference as a
/// dependency from the top-level type that contains the reference to the top-level
/// type that declares the symbol, or to an external assembly, or as unresolvable.
/// Namespace imports, references to the core library, and references to the
/// <c>[Vibecheck]</c> attribute itself are not recorded. A reference within one type is
/// not a dependency, and is recorded as a site instead, as is every constructor that
/// an object creation or a constructor initializer names.
/// </summary>
internal sealed class DependencyWalker : CSharpSyntaxWalker
{
    /// <summary>Formats a symbol as <see cref="RawDependency.Symbol"/> and <see cref="Declaration.Symbol"/> name it.</summary>
    internal static readonly SymbolDisplayFormat SymbolFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    private readonly SemanticModel model;
    private readonly NameBinder names;
    private readonly SourceResolver resolver;
    private readonly string file;
    private readonly string assembly;
    private readonly IAssemblySymbol coreLibrary;

    /// <summary>A dependency before duplicate names are qualified: the plain type names and the assemblies that declare them.</summary>
    internal sealed record Walked(string FromType, string ToType, string ToAssembly, string Kind, string Symbol, int Line);

    /// <summary>A site before duplicate names are qualified: the plain type names and the assembly that declares the symbol.</summary>
    internal sealed record WalkedSite(string Symbol, string FromType, string ToType, string ToAssembly, int Line);

    public List<Walked> Dependencies { get; } = [];
    public List<WalkedSite> Sites { get; } = [];
    public List<ExternalReference> ExternalReferences { get; } = [];
    public List<UnresolvableReference> Unresolvable { get; } = [];

    public DependencyWalker(SemanticModel model, Compilation compilation, SourceResolver resolver, string file)
    {
        this.model = model;
        names = new NameBinder(model);
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
            if (names.Bind(node).Symbol is INamedTypeSymbol or INamespaceSymbol) return;
        }

        var from = EnclosingTopLevelType(node);
        if (from is null) return;

        // The name is bound through the name binder, since the semantic model binds a whole initializer again for
        // each name in it that has no bound node, which makes a large generated table cost minutes.
        var symbol = names.Bind(node).SymbolOrCandidate;
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
            ExternalReferences.Add(new ExternalReference(fromName, assembly.Name, RoslynAnalyzer.OpenName(target), KindOf(node, symbol), display, file, line));
            return;
        }

        var targetName = target.ToDisplayString(RoslynAnalyzer.TypeNameFormat);
        if (targetName == RoslynAnalyzer.AttributeName) return;
        if (SymbolEqualityComparer.Default.Equals(target, from))
        {
            Sites.Add(new WalkedSite(display, fromName, targetName, target.ContainingAssembly?.Name ?? "", line));
            return;
        }
        var paths = target.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).ToList();
        if (!paths.Any(p => resolver.SourceOf(p) is not null))
        {
            Unresolvable.Add(new UnresolvableReference(fromName, display, resolver.ReasonFor(paths.FirstOrDefault()), file, line));
            return;
        }
        Dependencies.Add(new Walked(fromName, targetName, target.ContainingAssembly?.Name ?? "", KindOf(node, symbol), display, line));
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        RecordConstructor(node);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        RecordConstructor(node);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        RecordConstructor(node);
        base.VisitConstructorInitializer(node);
    }

    /// <summary>
    /// Records a site for the constructor that a node invokes when the constructor is declared in source in the
    /// repository. The type's name in an object creation is recorded as a reference to the type, which says nothing
    /// about which constructor was called.
    /// </summary>
    private void RecordConstructor(SyntaxNode node)
    {
        if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor) return;
        constructor = constructor.OriginalDefinition;
        if (!constructor.DeclaringSyntaxReferences.Any(r => resolver.SourceOf(r.SyntaxTree.FilePath) is not null)) return;
        var from = EnclosingTopLevelType(node);
        var target = TopLevelTypeOf(constructor);
        if (from is null || target is null) return;
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        Sites.Add(new WalkedSite(constructor.ToDisplayString(SymbolFormat), from.ToDisplayString(RoslynAnalyzer.TypeNameFormat),
            target.ToDisplayString(RoslynAnalyzer.TypeNameFormat), target.ContainingAssembly?.Name ?? "", line));
    }

    /// <summary>
    /// Returns the top-level type whose declaration contains a node, or <see langword="null"/> outside any type, as in an
    /// assembly attribute. A type's base list and constraints belong to the type, although the semantic model places
    /// them in the enclosing namespace.
    /// </summary>
    private INamedTypeSymbol? EnclosingTopLevelType(SyntaxNode node)
    {
        var declaration = node.FirstAncestorOrSelf<SyntaxNode>(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
        var enclosing = declaration is null ? model.GetEnclosingSymbol(node.SpanStart) : model.GetDeclaredSymbol(declaration);
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
