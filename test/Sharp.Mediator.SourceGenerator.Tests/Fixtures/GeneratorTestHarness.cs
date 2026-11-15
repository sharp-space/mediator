using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sharp.Mediator.SourceGenerator.Tests.Fixtures;

internal static class GeneratorTestHarness
{
    // The full runtime closure (TRUSTED_PLATFORM_ASSEMBLIES) so "generated code compiles" is asserted against real
    // references — the test host's dependency set already includes Sharp.Mediator and the DI abstractions.
    internal static readonly MetadataReference[] References = BuildReferences();

    internal static GeneratorRun RunGenerator(
        string source,
        MetadataReference[]? extraReferences = null,
        string? assemblyName = "GeneratorFixture",
        CSharpParseOptions? parseOptions = null,
        IncrementalGeneratorOutputKind disabledOutputs = IncrementalGeneratorOutputKind.None,
        ReportDiagnostic generalDiagnosticOption = ReportDiagnostic.Default,
        IReadOnlyDictionary<string, string>? buildProperties = null
    )
    {
        parseOptions ??= new CSharpParseOptions(LanguageVersion.Preview);

        // A file path mirrors real builds; diagnostics carry source locations only when the tree has one.
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, "GeneratorFixture.cs");

        // ReportDiagnostic.Error is what TreatWarningsAsErrors passes the compiler; MSBuild's -warnaserror
        // switch does not.
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            [.. References, .. extraReferences ?? []],
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: generalDiagnosticOption));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new MediatorGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            optionsProvider: buildProperties is null ? null : BuildProperties(buildProperties),
            driverOptions: new GeneratorDriverOptions(disabledOutputs));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        diagnostics = diagnostics.AddRange(compilation.WithAnalyzers([new MediatorAnalyzer()])
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult());

        return new GeneratorRun(diagnostics, driver.GetRunResult(), outputCompilation);
    }

    private static MetadataReference[] BuildReferences()
    {
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return
        [
            .. trustedAssemblies.Split(Path.PathSeparator)
                .Select(static MetadataReference (x) => MetadataReference.CreateFromFile(x))
        ];
    }

    // Warnings in generated code count as failures: consumers building with TreatWarningsAsErrors cannot fix them.
    internal static void AssertCompiles(GeneratorRun run)
    {
        var diagnostics = run.OutputCompilation.GetDiagnostics();
        var errors = diagnostics
            .Where(static x => x.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            $"user + generated code must compile, got: {string.Join(" | ", errors.Take(5))}");

        var generatedWarnings = diagnostics
            .Where(static x => x.Severity == DiagnosticSeverity.Warning &&
                x.Location.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) == true)
            .ToList();

        Assert.True(generatedWarnings.Count == 0,
            $"generated code must be warning-free, got: {string.Join(" | ", generatedWarnings.Take(5))}");
    }

    private static string GeneratedText(GeneratorRun run, string hintName)
    {
        var generator = Assert.Single(run.Result.Results);
        var generated = Assert.Single(generator.GeneratedSources, x => x.HintName == hintName);
        return generated.SourceText.ToString();
    }

    internal static string RegistrationsText(GeneratorRun run) => GeneratedText(run, "SharpMediator.Registrations.g.cs");

    /// <summary>
    /// The global options a build hands the generator, keyed as the generated .editorconfig writes them, such as <c>build_property.RunAnalyzers</c>.
    /// </summary>
    internal static AnalyzerConfigOptionsProvider BuildProperties(IReadOnlyDictionary<string, string> values) =>
        new BuildPropertyOptionsProvider(values);

    /// <summary>A compilation without handlers emits only the registrar telling AddHandlers the generator ran.</summary>
    internal static void AssertOnlyHandlerRegistrarMarker(GeneratorRun run)
    {
        var source = Assert.Single(Assert.Single(run.Result.Results).GeneratedSources);
        Assert.Equal("SharpMediator.Registrations.g.cs", source.HintName);

        var text = source.SourceText.ToString();
        Assert.Contains("GeneratedHandlerRegistrar", text);
        Assert.DoesNotContain("GeneratedMediatorRegistrar", text);
    }

    internal static string AllGeneratedText(GeneratorRun run) =>
        string.Join("\n", run.Result.Results.SelectMany(static x => x.GeneratedSources)
            .Select(static x => x.SourceText.ToString()));

    internal static MetadataReference CompileToReference(string assemblyName, string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success,
            $"reference assembly must compile, got: {string.Join(" | ", emitted.Diagnostics.Where(static x => x.Severity == DiagnosticSeverity.Error).Take(5))}");

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    internal static MetadataReference CompileGeneratedToReference(
        string assemblyName,
        string source,
        MetadataReference[]? extraReferences = null
    )
    {
        var run = RunGenerator(source, extraReferences, assemblyName);
        AssertCompiles(run);

        using var stream = new MemoryStream();
        var emitted = run.OutputCompilation.Emit(stream);
        Assert.True(emitted.Success,
            $"generated reference assembly must compile, got: {string.Join(" | ", emitted.Diagnostics.Where(static x => x.Severity == DiagnosticSeverity.Error).Take(5))}");

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    internal static Assembly CompileGeneratedToAssembly(string assemblyName, string source)
    {
        var run = RunGenerator(source, assemblyName: assemblyName);
        AssertCompiles(run);

        using var stream = new MemoryStream();
        var emitted = run.OutputCompilation.Emit(stream);
        Assert.True(emitted.Success,
            $"generated runtime assembly must compile, got: {string.Join(" | ", emitted.Diagnostics.Where(static x => x.Severity == DiagnosticSeverity.Error).Take(5))}");

        return Assembly.Load(stream.ToArray());
    }

    internal static Assembly CompileToAssembly(string assemblyName, string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success,
            $"runtime fixture assembly must compile, got: {string.Join(" | ", emitted.Diagnostics.Where(static x => x.Severity == DiagnosticSeverity.Error).Take(5))}");

        return Assembly.Load(stream.ToArray());
    }

    /// <summary>What a middleware closer emits when it registers <paramref name="implementation" />: its last type argument.</summary>
    internal static string ClosedOver(string implementation) => $", {implementation}>(middlewareType);";

    internal static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    internal sealed record GeneratorRun(
        ImmutableArray<Diagnostic> Diagnostics,
        GeneratorDriverRunResult Result,
        Compilation OutputCompilation
    );

    private sealed class BuildPropertyOptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions None = new Options(new Dictionary<string, string>());

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(values);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => None;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => None;

        private sealed class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
                values.TryGetValue(key, out value);
        }
    }
}