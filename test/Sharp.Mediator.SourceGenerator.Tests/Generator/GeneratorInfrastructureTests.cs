using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sharp.Mediator.SourceGenerator.Analysis;
using Sharp.Mediator.SourceGenerator.Generation;
using Sharp.Mediator.SourceGenerator.Tests.Fixtures;

namespace Sharp.Mediator.SourceGenerator.Tests.Generator;

public sealed class GeneratorInfrastructureTests
{
    private const string IncrementalFixtureSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Sharp.Mediator;
        namespace GeneratorFixture;

        public sealed record Ping : IRequest<int>;

        public sealed class PingHandler : IRequestHandler<Ping, int>
        {
            public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
        }

        public sealed class Trace<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
        {
            public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                next(message, cancellationToken);
        }
        """;

    [Fact]
    public void AssemblyWithoutInformationalMetadata_UsesAssemblyVersion()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("NoMetadata") { Version = new Version(3, 4, 5, 6) },
            AssemblyBuilderAccess.Run);

        Assert.Equal("3.4.5.6", Emitter.ResolveToolVersion(assembly));
    }

    [Fact]
    public void UnnamedCompilation_UsesStableGeneratedNamespace()
    {
        var run = GeneratorTestHarness.RunGenerator(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            internal sealed record Ping : IRequest<int>;
            internal sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
            }
            """, assemblyName: null);

        Assert.Null(run.OutputCompilation.AssemblyName);
        GeneratorTestHarness.AssertCompiles(run);
        Assert.Contains("namespace Sharp.Mediator.Generated.AUnnamedAssembly", GeneratorTestHarness.AllGeneratedText(run));
    }

    // A project that leaves generated code out of its coverage by these attributes measures any generated type without
    // them, and nothing calls the constructor of the attribute that places the interceptors.
    [Fact]
    public void EveryGeneratedType_IsMarkedAsGeneratedCode()
    {
        var run = GeneratorTestHarness.RunGenerator(
            IncrementalFixtureSource +
            "public static class Calls { public static ValueTask<int> Call(IMediator mediator) => mediator.Request(new Ping()); }",
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview)
                .WithFeatures([new("InterceptorsNamespaces", "Sharp.Mediator.Generated.Interceptors")]));

        GeneratorTestHarness.AssertCompiles(run);

        var types = run.OutputCompilation.SyntaxTrees
            .Where(static x => x.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Select(declaration => run.OutputCompilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration)!))
            .Where(static x => x.ContainingType is null)
            .ToList();

        string[] generated =
            ["GeneratedHandlerRegistrar", "GeneratedMediatorRegistrar", "InterceptsLocationAttribute", "MediatorInterceptors"];

        Assert.Equal(generated, types.Select(static x => x.Name).Order(StringComparer.Ordinal));

        Assert.All(types, static type =>
        {
            var attributes = type.GetAttributes().Select(static x => x.AttributeClass?.ToDisplayString()).ToList();
            Assert.Contains("System.CodeDom.Compiler.GeneratedCodeAttribute", attributes);
            Assert.Contains("System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute", attributes);
        });

        // Marked as the other generated types are, the attribute still places the interceptor.
        var tree = run.OutputCompilation.SyntaxTrees.Single(static x => x.FilePath == "GeneratorFixture.cs");
        var call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static x => x.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Request" });

        var interceptor = run.OutputCompilation.GetSemanticModel(tree).GetInterceptorMethod(call);
        Assert.Equal("MediatorInterceptors", interceptor?.ContainingType.Name);
    }

    [Fact]
    public void EquatableArray_SameValues_AreEqualWithSameHash()
    {
        var first = new EquatableArray<string>(["a", "b"]);
        var same = new EquatableArray<string>(["a", "b"]);
        Assert.True(first.Equals(same));
        Assert.True(first.Equals((object)same));
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
    }

    [Fact]
    public void EquatableArray_DifferentValuesOrLengthOrType_AreNotEqual()
    {
        var first = new EquatableArray<string>(["a", "b"]);
        var differentValue = new EquatableArray<string>(["a", "c"]);
        var differentLength = new EquatableArray<string>(["a"]);

        Assert.False(first.Equals(differentValue));
        Assert.False(first.Equals(differentLength));
        Assert.False(first.Equals(new object()));
        Assert.False(first.Equals(null));
    }

    [Fact]
    public void EquatableArray_Default_IsEmptyWithZeroHash()
    {
        var empty = default(EquatableArray<string>);
        Assert.Empty(empty.Values);
        Assert.Equal(0, empty.GetHashCode());
    }

    [Fact]
    public void EquatableArray_Enumerators_YieldEveryValue()
    {
        var first = new EquatableArray<string>(["a", "b"]);
        Assert.Equal(["a", "b"], first.ToArray());

        var nonGenericEnumerator = ((IEnumerable)first).GetEnumerator();
        var nonGenericValues = new List<string>();

        using (nonGenericEnumerator as IDisposable)
        {
            while (nonGenericEnumerator.MoveNext())
                nonGenericValues.Add(Assert.IsType<string>(nonGenericEnumerator.Current));
        }

        Assert.Equal(["a", "b"], nonGenericValues);
    }

    [Fact]
    public void UnrelatedEdit_LeavesSourceEmissionCached()
    {
        // Regression: raw ImmutableArray models compare by reference, so every keystroke re-emitted the generated
        // sources even though the value-equatable records underneath had not changed.
        var reason = EmissionReasonAfterEdit(static (compilation, parseOptions, unrelated, _) => compilation.ReplaceSyntaxTree(
            unrelated,
            CSharpSyntaxTree.ParseText("public static class Unrelated { public static int Value => 2; }", parseOptions, "Unrelated.cs")));

        Assert.Equal(IncrementalStepRunReason.Cached, reason);
    }

    [Fact]
    // A middleware whose constraints name a variant type has the pairs that names do not match checked against the
    // compilation on every edit. The answer is the same list of names, so source emission stays cached.
    public void UnrelatedEdit_LeavesSourceEmissionCached_WithAVariantConstraint()
    {
        const string source =
            """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sharp.Mediator;
            namespace GeneratorFixture;

            public sealed record Names : IRequest<List<string>>;

            public sealed class NamesHandler : IRequestHandler<Names, List<string>>
            {
                public ValueTask<List<string>> Handle(Names message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<string>());
            }

            public sealed class Covariant<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                where TMessage : IMessage<TResponse>
                where TResponse : IEnumerable<object>
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                    next(message, cancellationToken);
            }
            """;

        var reason = StepReasonAfterEdit(
            MediatorGenerator.EmissionTrackingName,
            static (compilation, parseOptions, unrelated, _) => compilation.ReplaceSyntaxTree(unrelated,
                CSharpSyntaxTree.ParseText("public static class Unrelated { public static int Value => 2; }", parseOptions, "Unrelated.cs")),
            source);

        Assert.Equal(IncrementalStepRunReason.Cached, reason);
    }

    [Fact]
    public void MovingADeclaration_DoesNotReemitSources()
    {
        // Diagnostic locations stay out of the emission model, so shifting text above a handler changes nothing.
        var reason = EmissionReasonAfterEdit(static (compilation, parseOptions, _, handlers) => compilation.ReplaceSyntaxTree(
            handlers,
            CSharpSyntaxTree.ParseText("// shifted\n" + IncrementalFixtureSource, parseOptions, "Handlers.cs")));

        Assert.Contains(reason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged });
    }

    [Fact]
    public void SourceEdit_DoesNotRescanReferencedAssemblies()
    {
        // Regression: this scan walks referenced metadata and ran on every keystroke.
        var reason = StepReasonAfterEdit(
            MediatorGenerator.ReferenceScanTrackingName,
            static (compilation, parseOptions, unrelated, _) => compilation.ReplaceSyntaxTree(
                unrelated,
                CSharpSyntaxTree.ParseText("public static class Unrelated { public static int Value => 2; }", parseOptions, "Unrelated.cs")));

        Assert.Equal(IncrementalStepRunReason.Cached, reason);
    }

    [Fact]
    public void ReferenceChange_RescansReferencedAssemblies()
    {
        var reason = StepReasonAfterEdit(
            MediatorGenerator.ReferenceScanTrackingName,
            static (compilation, _, _, _) => compilation.RemoveReferences(compilation.References.Last()));

        Assert.NotEqual(IncrementalStepRunReason.Cached, reason);
    }

    [Fact]
    public void SameReferencesComparer_SameNameAndReferences_AreEqualWithSameHash()
    {
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var first = CSharpCompilation.Create("First", references: [reference]);
        var firstAgain = CSharpCompilation.Create("First", references: [reference]);
        var comparer = MediatorGenerator.SameReferencesComparer.Instance;
        Assert.True(comparer.Equals(first, firstAgain));
        Assert.Equal(comparer.GetHashCode(first), comparer.GetHashCode(firstAgain));
    }

    [Fact]
    public void SameReferencesComparer_DifferentNameOrReferences_AreNotEqual()
    {
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var first = CSharpCompilation.Create("First", references: [reference]);
        var renamed = CSharpCompilation.Create("Second", references: [reference]);
        var noReferences = CSharpCompilation.Create("First");
        var comparer = MediatorGenerator.SameReferencesComparer.Instance;
        Assert.False(comparer.Equals(first, renamed));
        Assert.False(comparer.Equals(first, noReferences));
    }

    private static IncrementalStepRunReason EmissionReasonAfterEdit(
        Func<CSharpCompilation, CSharpParseOptions, SyntaxTree, SyntaxTree, CSharpCompilation> edit
    ) => StepReasonAfterEdit(MediatorGenerator.EmissionTrackingName, edit);

    private static IncrementalStepRunReason StepReasonAfterEdit(
        string trackingName,
        Func<CSharpCompilation, CSharpParseOptions, SyntaxTree, SyntaxTree, CSharpCompilation> edit,
        string source = IncrementalFixtureSource
    )
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var handlers = CSharpSyntaxTree.ParseText(source, parseOptions, "Handlers.cs");
        var unrelated = CSharpSyntaxTree.ParseText("public static class Unrelated { public static int Value => 1; }", parseOptions,
            "Unrelated.cs");

        var compilation = CSharpCompilation.Create(
            "IncrementalFixture",
            [handlers, unrelated],
            GeneratorTestHarness.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new MediatorGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, true));

        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(edit(compilation, parseOptions, unrelated, handlers));
        var step = Assert.Single(driver.GetRunResult().Results.Single().TrackedSteps[trackingName]);
        return Assert.Single(step.Outputs).Reason;
    }

    [Theory]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    public void NormalizeToolVersion_PrefersInformationalVersion(string informational, string expected) =>
        Assert.Equal(expected, Emitter.NormalizeToolVersion(informational, new Version(9, 9)));

    [Fact]
    public void NormalizeToolVersion_FallsBackToAssemblyVersion_ThenStableDefault()
    {
        Assert.Equal("2.3.4.5", Emitter.NormalizeToolVersion(null, new Version(2, 3, 4, 5)));
        Assert.Equal("1.0.0.0", Emitter.NormalizeToolVersion("", null));
    }

    [Fact]
    public void EncodeAssemblyIdentifier_MixedCharacters_EscapesEveryNonAlphanumericCodeUnit() =>
        Assert.Equal("AAz09_002D_002E_005F_D55C", MediatorGenerator.EncodeAssemblyIdentifier("Az09-._한"));

    [Fact]
    public void EncodeAssemblyIdentifier_EmptyName_ReturnsA() => Assert.Equal("A", MediatorGenerator.EncodeAssemblyIdentifier(""));

    [Fact]
    public void Satisfies_KindFlags_MatchOnlyTheRequiredKind()
    {
        var reference = new TypeFacts(false, false, false, true, "Candidate;IContract;IMessage<int>", "Candidate");
        var value = new TypeFacts(true, false, false, true, "int", "int");
        var nullableValue = new TypeFacts(true, true, false, true, "int?", "int?");
        var unmanaged = new TypeFacts(true, false, true, true, "int", "int");

        Assert.False(Emitter.Satisfies(value, Requirements(true), [], []));
        Assert.False(Emitter.Satisfies(reference, Requirements(value: true), [], []));
        Assert.True(Emitter.Satisfies(value, Requirements(value: true), [], []));
        Assert.False(Emitter.Satisfies(nullableValue, Requirements(value: true), [], []));
        Assert.False(Emitter.Satisfies(value, Requirements(unmanaged: true), [], []));
        Assert.True(Emitter.Satisfies(unmanaged, Requirements(unmanaged: true), [], []));

        Assert.False(Emitter.Satisfies(
            reference with { HasParameterlessConstructor = false }, Requirements(constructor: true), [], []));
    }

    [Fact]
    public void Satisfies_ExactTypes_RequireTheListedTypes()
    {
        var reference = new TypeFacts(false, false, false, true, "Candidate;IContract;IMessage<int>", "Candidate");
        Assert.True(Emitter.Satisfies(reference, Requirements(exact: ";IContract"), [], []));
        Assert.False(Emitter.Satisfies(reference, Requirements(exact: "IMissing"), [], []));
    }

    [Fact]
    public void Satisfies_SubstitutedTypes_AreComparedAfterSubstitution()
    {
        var reference = new TypeFacts(false, false, false, true, "Candidate;IContract;IMessage<int>", "Candidate");
        Assert.True(Emitter.Satisfies(
            reference, Requirements(substitutable: ";IMessage<TResponse>"), ["TResponse"], ["int"]));

        Assert.False(Emitter.Satisfies(
            reference, Requirements(substitutable: "IMessage<TResponse>"), ["TResponse"], ["string"]));
    }

    [Fact]
    public void Satisfies_NoRequirements_AlwaysHolds()
    {
        var reference = new TypeFacts(false, false, false, true, "Candidate;IContract;IMessage<int>", "Candidate");
        Assert.True(Emitter.Satisfies(reference, Requirements(), [], []));
    }

    [Fact]
    public void SubstituteTypeParameters_ReplacesWholeTokensOnly()
    {
        Assert.Equal(
            "global::Box<int, TMissing>.TResponse2",
            Emitter.SubstituteTypeParameters(
                "global::Box<TResponse, TMissing>.TResponse2",
                ["TResponse", "TMissing"],
                ["int"]));

        Assert.Equal("", Emitter.SubstituteTypeParameters("", [], []));
    }

    [Fact]
    public void IsIdentifierCharacter_AcceptsLettersDigitsUnderscoreAndAt()
    {
        Assert.True(Emitter.IsIdentifierCharacter('A'));
        Assert.True(Emitter.IsIdentifierCharacter('7'));
        Assert.True(Emitter.IsIdentifierCharacter('_'));
        Assert.True(Emitter.IsIdentifierCharacter('@'));
        Assert.False(Emitter.IsIdentifierCharacter('.'));
    }

    [Fact]
    public void SimpleName_StripsNamespaceAndGenericArguments()
    {
        Assert.Equal("Ping", SymbolNames.SimpleName("global::Example.Ping<int>"));
        Assert.Equal("Ping", SymbolNames.SimpleName("Ping"));
        Assert.Equal("Ping", SymbolNames.SimpleName("Example.Ping"));
        Assert.Equal("Ping", SymbolNames.SimpleName("global::Example.Ping<global::System.Collections.Generic.List<int>>"));

        // A type nested in a generic one is named by its own name, not by the generic type's.
        Assert.Equal("Message", SymbolNames.SimpleName("global::Example.Box<global::Example.Host.Secret>.Message"));
    }

    [Fact]
    public void ContainsTypeParameter_ParameterConstructedArrayOrNestedType_ReturnsTrue()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText("public sealed class Sample<T> { public interface INested; }");
        var compilation = CSharpCompilation.Create("TypeSymbols", [syntaxTree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var sample = compilation.GetTypeByMetadataName("Sample`1")!;
        var parameter = sample.TypeParameters[0];
        var constructed = sample.Construct(parameter);
        var array = compilation.CreateArrayTypeSymbol(parameter);
        var nested = constructed.GetTypeMembers("INested").Single();

        Assert.True(Discovery.ContainsTypeParameter(parameter));
        Assert.True(Discovery.ContainsTypeParameter(constructed));
        Assert.True(Discovery.ContainsTypeParameter(array));
        Assert.True(Discovery.ContainsTypeParameter(nested));
    }

    [Fact]
    public void ContainsTypeParameter_DynamicOrSpecialType_ReturnsFalse()
    {
        var compilation = CSharpCompilation.Create("TypeSymbols",
            [CSharpSyntaxTree.ParseText("public sealed class Sample<T> { public interface INested; }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        Assert.False(Discovery.ContainsTypeParameter(compilation.DynamicType));
        Assert.False(Discovery.ContainsTypeParameter(compilation.GetSpecialType(SpecialType.System_Int32)));
    }

    private static TypeParameterRequirements Requirements(
        bool reference = false,
        bool value = false,
        bool unmanaged = false,
        bool constructor = false,
        string exact = "",
        string substitutable = ""
    ) => new(reference, value, unmanaged, constructor, exact, substitutable);
}