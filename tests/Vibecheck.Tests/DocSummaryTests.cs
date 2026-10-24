using Vibecheck.Analysis;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the first sentence a documentation comment gives a declaration, which the glossary shows.</summary>
public class DocSummaryTests
{
    [Fact]
    public void TakesTheFirstSentenceOfTheSummary()
    {
        const string xml = "<member name=\"T:A.Clock\"><summary>\n  Tells the time.\n  It is never wrong.\n</summary><remarks>Not this.</remarks></member>";
        Assert.Equal("Tells the time.", DocSummary.FirstSentence(xml));
        Assert.Null(DocSummary.FirstSentence("<member><remarks>No summary.</remarks></member>"));
        Assert.Null(DocSummary.FirstSentence("not xml <"));
    }

    [Fact]
    public void ReadsEachReferenceAsTheNameItRefersTo()
    {
        const string xml = "<member name=\"P:A.Term.File\"><summary>Gets the file, or <see langword=\"null\"/> for a term the <see cref=\"T:A.Map\"/> introduces, as <see cref=\"M:A.Glossary.Build(A.Changeset)\"/> lists it from <paramref name=\"cs\"/>; see <c>docs/attention.md</c>.</summary></member>";
        Assert.Equal("Gets the file, or null for a term the Map introduces, as Glossary.Build lists it from cs; see docs/attention.md.", DocSummary.FirstSentence(xml));
        // An unresolved reference keeps the name as written, and a reference with text shows the text.
        Assert.Equal("See Nowhere and the docs.", DocSummary.FirstSentence("<summary>See <see cref=\"!:Nowhere\"/> and <see href=\"https://example.test\">the docs</see>.</summary>"));
    }
}
