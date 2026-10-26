using Vibecheck.Analysis;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>
/// Checks the analysis of a solution that builds its own source generator, which a restore alone leaves uncompiled.
/// The name keeps it out of the quick loop's <c>FullyQualifiedName!~Sample</c> filter, since it runs dotnet.
/// </summary>
public class SampleGeneratorProjectTests
{
    private const string Solution = """
        <Solution>
          <Project Path="src/Gen/Gen.csproj" />
          <Project Path="src/App/App.csproj" />
        </Solution>
        """;

    private const string GeneratorProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>netstandard2.0</TargetFramework>
            <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
          </ItemGroup>
        </Project>
        """;

    private const string Generator = """
        using Microsoft.CodeAnalysis;

        namespace Gen
        {
            [Generator]
            public sealed class Hello : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context) =>
                    context.RegisterPostInitializationOutput(c => c.AddSource("Made.g.cs", "namespace App.Generated { public class Made { } }"));
            }
        }
        """;

    private const string AppProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="..\Gen\Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
          </ItemGroup>
        </Project>
        """;

    private const string App = """
        namespace App
        {
            public class Uses
            {
                public App.Generated.Made? Made;
            }
        }
        """;

    [Fact]
    public async Task ARestoredSolutionBuildsItsOwnGeneratorAndAnalyzesWhatItGenerates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vibecheck-gen-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Write(dir, "Gen.slnx", Solution);
            Write(dir, "src/Gen/Gen.csproj", GeneratorProject);
            Write(dir, "src/Gen/Hello.cs", Generator);
            Write(dir, "src/App/App.csproj", AppProject);
            Write(dir, "src/App/Uses.cs", App);
            var solution = Path.Combine(dir, "Gen.slnx");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await SolutionBuild.RunAsync(solution, SolutionPreparation.Restore, timeout.Token);

            var reports = new List<string>();
            var analysis = await RoslynAnalyzer.AnalyzeAsync(dir, solution, new MapGeneratorTests.Progress(reports), timeout.Token);
            Assert.Contains(analysis.Types, t => t.Name == "App.Generated.Made");
            Assert.Contains(analysis.Dependencies, d => d.FromType == "App.Uses" && d.ToType == "App.Generated.Made");
            Assert.Contains("building generator Gen", reports);
            // The app's bin directory may exist, empty, but its assembly is never compiled.
            Assert.Empty(Directory.EnumerateFiles(dir, "App.dll", SearchOption.AllDirectories));
            Assert.Empty(analysis.Warnings);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Write(string dir, string relative, string text)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
