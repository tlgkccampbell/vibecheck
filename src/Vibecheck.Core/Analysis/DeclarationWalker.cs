using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vibecheck.Analysis;

/// <summary>
/// Walks one document's syntax tree and records every type it declares, nested or not, and every member of those
/// types, as a <see cref="Declaration"/>. Local functions, accessors and the members a compiler synthesizes are not
/// declarations. See <c>docs/attention.md</c> section 3.
/// </summary>
internal sealed class DeclarationWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel model;
    private readonly string file;
    private readonly TextLineCollection lines;

    public DeclarationWalker(SemanticModel model, string file)
    {
        this.model = model;
        this.file = file;
        lines = model.SyntaxTree.GetText().Lines;
    }

    /// <summary>Gets the declarations found so far, with each one's top-level type named by its plain name.</summary>
    public List<Declaration> Declarations { get; } = [];

    public override void VisitClassDeclaration(ClassDeclarationSyntax node) { AddType(node); base.VisitClassDeclaration(node); }
    public override void VisitStructDeclaration(StructDeclarationSyntax node) { AddType(node); base.VisitStructDeclaration(node); }
    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) { AddType(node); base.VisitInterfaceDeclaration(node); }
    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) { AddType(node); base.VisitRecordDeclaration(node); }
    public override void VisitEnumDeclaration(EnumDeclarationSyntax node) { AddType(node); base.VisitEnumDeclaration(node); }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node) =>
        Add(node, node, "type", node.Identifier, []);

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node) =>
        Add(node, node, "method", node.Identifier, Bodies(node.Body, node.ExpressionBody), test: true);

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
        Add(node, node, "constructor", node.Identifier, Bodies(node.Initializer, node.Body, node.ExpressionBody));

    public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node) =>
        Add(node, node, "method", node.Identifier, Bodies(node.Body, node.ExpressionBody));

    public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node) =>
        Add(node, node, "operator", default, Bodies(node.Body, node.ExpressionBody));

    public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) =>
        Add(node, node, "operator", default, Bodies(node.Body, node.ExpressionBody));

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
        Add(node, node, "property", node.Identifier, Bodies([node.ExpressionBody, node.Initializer, .. AccessorBodies(node.AccessorList)]));

    public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node) =>
        Add(node, node, "indexer", default, Bodies([node.ExpressionBody, .. AccessorBodies(node.AccessorList)]));

    public override void VisitEventDeclaration(EventDeclarationSyntax node) =>
        Add(node, node, "event", node.Identifier, Bodies([.. AccessorBodies(node.AccessorList)]));

    public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) =>
        Add(node, node, "field", node.Identifier, Bodies(node.EqualsValue));

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node) => AddVariables(node, node.Declaration, "field");

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) => AddVariables(node, node.Declaration, "event");

    /// <summary>Adds a type, whose shape leaves out its members, which are declarations of their own.</summary>
    private void AddType(BaseTypeDeclarationSyntax node)
    {
        var members = node switch
        {
            TypeDeclarationSyntax t => t.Members.Select(m => m.Span),
            EnumDeclarationSyntax e => e.Members.Select(m => m.Span),
            _ => [],
        };
        var bodyLine = node.OpenBraceToken.IsKind(SyntaxKind.None) || node.OpenBraceToken.IsMissing ? (int?)null : LineOf(node.OpenBraceToken.SpanStart);
        Add(node, node, "type", node.Identifier, [], members.ToList(), bodyLine);
    }

    /// <summary>Adds each variable of a field or an event field, whose shape is the declaration without its variables and whose body is the variable's initializer.</summary>
    private void AddVariables(MemberDeclarationSyntax node, VariableDeclarationSyntax declaration, string kind)
    {
        foreach (var variable in declaration.Variables)
            Add(node, variable, kind, variable.Identifier, Bodies(variable.Initializer), [declaration.Variables.FullSpan]);
    }

    private static List<TextSpan> Bodies(params SyntaxNode?[] nodes) => nodes.Where(n => n is not null).Select(n => n!.Span).ToList();

    private static IEnumerable<SyntaxNode?> AccessorBodies(AccessorListSyntax? list) =>
        list is null ? [] : list.Accessors.SelectMany(a => new SyntaxNode?[] { a.Body, a.ExpressionBody });

    /// <summary>Adds the declaration of the symbol that a syntax declares.</summary>
    /// <param name="syntax">The syntax whose tokens make the shape and whose lines the declaration covers.</param>
    /// <param name="declarator">The syntax that declares the symbol, which differs from the syntax for a field's variables.</param>
    /// <param name="kind">The kind of declaration.</param>
    /// <param name="name">The name token that the shape leaves out, or the default token for a declaration whose name is not an identifier.</param>
    /// <param name="bodies">The spans whose tokens make the body.</param>
    /// <param name="excluded">The spans whose tokens belong to neither the shape nor the body.</param>
    /// <param name="bodyLine">The line the body begins on, when it is not the first line of the first body span.</param>
    /// <param name="test">Whether the declaration can be a test method, whose test framework attribute is recorded.</param>
    private void Add(SyntaxNode syntax, SyntaxNode declarator, string kind, SyntaxToken name, List<TextSpan> bodies, List<TextSpan>? excluded = null, int? bodyLine = null, bool test = false)
    {
        if (model.GetDeclaredSymbol(declarator) is not { } symbol) return;
        var top = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (top is null) return;
        while (top.ContainingType is not null) top = top.ContainingType;
        var selfNames = new HashSet<string>(StringComparer.Ordinal);
        for (var t = symbol as INamedTypeSymbol ?? symbol.ContainingType; t is not null; t = t.ContainingType) selfNames.Add(t.Name);

        var shape = new StringBuilder();
        var body = new StringBuilder();
        foreach (var token in syntax.DescendantTokens())
        {
            if (token == name) continue;
            if (excluded is not null && excluded.Any(s => s.Contains(token.Span))) continue;
            var target = bodies.Any(s => s.Contains(token.Span)) ? body : shape;
            target.Append(token.RawKind).Append(':').Append(Normalized(token, symbol.Name, selfNames)).Append(' ');
        }
        var first = LineOf(syntax.SpanStart);
        var last = LineOf(Math.Max(syntax.SpanStart, syntax.Span.End - 1));
        bodyLine ??= bodies.Count == 0 ? null : LineOf(bodies.Min(s => s.Start));
        var container = symbol.ContainingType?.ToDisplayString(DependencyWalker.SymbolFormat);
        Declarations.Add(new Declaration(
            symbol.ToDisplayString(DependencyWalker.SymbolFormat),
            top.ToDisplayString(RoslynAnalyzer.TypeNameFormat),
            symbol is INamedTypeSymbol { ContainingType: null } ? null : container,
            kind,
            AccessibilityOf(symbol.DeclaredAccessibility),
            IsExposed(symbol),
            file, first, last, bodyLine,
            Hash(shape.ToString()),
            bodies.Count == 0 ? null : Hash(body.ToString()),
            DocSummary.FirstSentence(symbol.GetDocumentationCommentXml()),
            test ? TestAttributeOf(symbol) : null));
        if (test && Declarations[^1].Test is not null)
            Declarations[^1] = Declarations[^1] with { Skipped = IsSkipped(symbol), Assertions = AssertionsIn(syntax) };
    }

    /// <summary>Returns a value indicating whether a test method carries an attribute that skips it, as <see cref="TestFrameworks.Skips"/> says.</summary>
    private static bool IsSkipped(ISymbol symbol) => symbol.GetAttributes().Any(a => a.AttributeClass is { ContainingAssembly: { } assembly } type
        && TestFrameworks.Skips(assembly.Name, type.Name, a.NamedArguments.Where(n => n.Value.Value is string { Length: > 0 }).Select(n => n.Key)));

    /// <summary>
    /// Returns the lines of each assertion in a method: each call into an assertion library that no other such call holds
    /// within the same function, so that a chain such as <c>x.Should().Be(3)</c> or a constraint such as
    /// <c>Is.EqualTo(3)</c> inside <c>Assert.That</c> counts once, and each assertion in a lambda counts on its own.
    /// </summary>
    private List<LineRange> AssertionsIn(SyntaxNode method)
    {
        bool IsAssertion(InvocationExpressionSyntax call) => model.GetSymbolInfo(call).Symbol is IMethodSymbol m
            && m.ContainingAssembly is { } assembly && TestFrameworks.IsAssertion(assembly.Name, m.ContainingType.Name);
        var result = new List<LineRange>();
        foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsAssertion(call)) continue;
            var held = call.Ancestors().TakeWhile(n => n != method && n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<InvocationExpressionSyntax>().Any(IsAssertion);
            if (!held) result.Add(new LineRange(LineOf(call.SpanStart), LineOf(Math.Max(call.SpanStart, call.Span.End - 1))));
        }
        return result;
    }

    /// <summary>
    /// Returns the name of the test framework attribute that a method carries, without its <c>Attribute</c> suffix, or
    /// <see langword="null"/> when it carries none. An attribute counts when its type, or a type it derives from, is one
    /// of <see cref="TestFrameworks.TestAttributes"/> declared in a test framework assembly, so that an attribute such as
    /// <c>SkippableFact</c>, which derives from xUnit's <c>Fact</c>, counts too.
    /// </summary>
    private static string? TestAttributeOf(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            for (var type = attribute.AttributeClass; type is not null; type = type.BaseType)
            {
                if (TestFrameworks.TestAttributes.Contains(type.Name) && type.ContainingAssembly is { } assembly && TestFrameworks.IsFramework(assembly.Name))
                    return attribute.AttributeClass!.Name.EndsWith("Attribute", StringComparison.Ordinal) ? attribute.AttributeClass.Name[..^"Attribute".Length] : attribute.AttributeClass.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns a token's text with the declaration's own name and the names of the types that contain it replaced by
    /// placeholders, so that renaming a declaration or its type leaves the hashes of a recursive call or a reference
    /// to the type unchanged.
    /// </summary>
    private static string Normalized(SyntaxToken token, string own, HashSet<string> types)
    {
        if (!token.IsKind(SyntaxKind.IdentifierToken)) return token.Text;
        if (token.ValueText == own) return "$name";
        return types.Contains(token.ValueText) ? "$type" : token.Text;
    }

    private int LineOf(int position) => lines.GetLineFromPosition(position).LineNumber + 1;

    /// <summary>Returns a value indicating whether code outside the assembly can see a symbol.</summary>
    private static bool IsExposed(ISymbol symbol)
    {
        for (var s = symbol; s is not null && s is not INamespaceSymbol; s = s.ContainingType)
        {
            if (s.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) return false;
        }
        return true;
    }

    private static string AccessibilityOf(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "",
    };

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
