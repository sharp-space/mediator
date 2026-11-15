using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using static Sharp.Mediator.SourceGenerator.Tests.Fixtures.GeneratorTestHarness;

namespace Sharp.Mediator.SourceGenerator.Tests.Diagnostics;

public sealed class AnalyzerSeparationTests
{
    private const string InvalidHandler =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Sharp.Mediator;
        public sealed class Query : IRequest<int>, IRequest<string>;
        public sealed class Handler : IRequestHandler<Query, int>
        {
            public ValueTask<int> Handle(Query message, CancellationToken token) => new(1);
        }
        """;

    [Fact]
    // The analyzer reports what the shared analysis finds without filtering it, and Roslyn turns a diagnostic an
    // analyzer does not list into AD0001, so it lists every descriptor but SMD020, which only source emission reports.
    public void Analyzer_ListsEveryDescriptorButTheOneSourceEmissionReports()
    {
        var descriptors = typeof(DiagnosticDescriptors)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(static x => x.GetValue(null))
            .OfType<DiagnosticDescriptor>()
            .Where(static x => !ReferenceEquals(x, DiagnosticDescriptors.LanguageVersionTooOld))
            .Select(static x => x.Id)
            .Order();

        Assert.Equal(descriptors, new MediatorAnalyzer().SupportedDiagnostics.Select(static x => x.Id).Order());
    }

    [Fact]
    public async Task Analyzer_WithoutGenerator_ReportsAtSourceLocation()
    {
        var compilation = Compile(InvalidHandler);
        var diagnostics = await Analyze(compilation);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SMD001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Same(compilation.SyntaxTrees.Single(), diagnostic.Location.SourceTree);

        Assert.Equal("Handler",
            (await diagnostic.Location.SourceTree!.GetTextAsync(TestContext.Current.CancellationToken)).ToString(diagnostic.Location.SourceSpan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suppression_ForInvalidInput_DoesNotEnableGeneration(bool projectSuppression)
    {
        var compilation = Compile(projectSuppression ? InvalidHandler : "#pragma warning disable SMD001\n" + InvalidHandler);
        if (projectSuppression)
        {
            compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("SMD001", ReportDiagnostic.Suppress)));
        }

        Assert.Empty(await Analyze(compilation));

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new MediatorGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.Single().Options);

        var result = driver.RunGenerators(compilation).GetRunResult();
        Assert.Empty(result.Diagnostics);

        var generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.ToString()));
        Assert.DoesNotContain("global::Query", generated);
        Assert.DoesNotContain("global::Handler", generated);
    }

    [Fact]
    public async Task ProjectConfiguration_ChangesSeverity_WithoutDuplicateDiagnostics()
    {
        var compilation = Compile(InvalidHandler);
        compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
            ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("SMD001", ReportDiagnostic.Warn)));

        var diagnostic = Assert.Single(await Analyze(compilation));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Empty(RunGenerator(InvalidHandler).Result.Diagnostics);
    }

    [Theory]
    [InlineData("false", null, true)]
    [InlineData("False", "", true)]
    [InlineData("", "false", true)]
    [InlineData(null, "false", true)]
    [InlineData("true", "false", false)]
    [InlineData("", "true", false)]
    [InlineData(null, null, false)]
    // Source emission leaves out what the analyzers report as an error, so a build that skips analyzers, through
    // RunAnalyzers=false or RunAnalyzersDuringBuild=false, compiled without the declaration and failed only at the
    // first dispatch. The generator reports the analyzers' diagnostics in such a build, by the SDK's rule: RunAnalyzers
    // decides when it is set, RunAnalyzersDuringBuild otherwise, and a property that is not set arrives empty.
    public void Generator_ReportsTheAnalyzerDiagnostics_WhenTheBuildSkipsAnalyzers(
        string? runAnalyzers,
        string? runAnalyzersDuringBuild,
        bool reports
    )
    {
        var properties = new Dictionary<string, string>();
        if (runAnalyzers is not null) properties["build_property.RunAnalyzers"] = runAnalyzers;
        if (runAnalyzersDuringBuild is not null) properties["build_property.RunAnalyzersDuringBuild"] = runAnalyzersDuringBuild;

        var diagnostics = RunGenerator(InvalidHandler, buildProperties: properties).Result.Diagnostics;
        if (!reports)
        {
            Assert.Empty(diagnostics);
            return;
        }

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SMD001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Handler", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    // The IDE runs no implementation output, and its live analysis runs the analyzers, so it never sees
    // a diagnostic twice.
    public void Generator_ReportsNothing_WhereImplementationOutputsDoNotRun()
    {
        var run = RunGenerator(
            InvalidHandler, disabledOutputs: IncrementalGeneratorOutputKind.Implementation,
            buildProperties: new Dictionary<string, string> { ["build_property.RunAnalyzers"] = "false" });

        Assert.Empty(run.Result.Diagnostics);
    }

    [Theory]
    [InlineData(ReportDiagnostic.Warn)]
    [InlineData(ReportDiagnostic.Suppress)]
    // The generator reports the analyzers' own diagnostics, so the project's severity settings apply to them
    // as they would.
    public void Generator_FollowsProjectSeverity_WhenTheBuildSkipsAnalyzers(ReportDiagnostic configured)
    {
        var compilation = Compile(InvalidHandler);
        compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
            ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("SMD001", configured)));

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new MediatorGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.Single().Options,
            optionsProvider: BuildProperties(new Dictionary<string, string> { ["build_property.RunAnalyzers"] = "false" }));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        if (configured == ReportDiagnostic.Suppress) Assert.Empty(diagnostics);
        else Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(diagnostics).Severity);
    }

    private static CSharpCompilation Compile(string source) => CSharpCompilation.Create(
        "AnalyzerFixture", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.cs")],
        References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static Task<ImmutableArray<Diagnostic>> Analyze(Compilation compilation) =>
        compilation.WithAnalyzers([new MediatorAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
}