using System.Reflection;
using Sharp.Mediator.Bindings;

namespace Sharp.Mediator.Tests.Registration;

public sealed class MediatorRuntimeTests
{
    [Fact]
    // Generated code passes the contract it was generated for, so a later version can read the contracts it supersedes
    // by number. One this version does not read fails before the assembly contributes anything, naming the assembly,
    // since it fails in a module initializer, which a stack trace does not tie to its assembly under Native AOT.
    public void AddAssembly_OfAContractThisVersionDoesNotRead_ThrowsNamingTheAssemblyAndBothContracts()
    {
        var exception = Assert.Throws<InvalidOperationException>(static () => MediatorRuntime.AddAssembly(
            2, typeof(MediatorRuntimeTests).Assembly, static _ => null, static _ => null, null, static _ => null,
            static () => [], null));

        Assert.Contains("'Sharp.Mediator.Tests'", exception.Message);
        Assert.Contains("contract 2", exception.Message);
        Assert.Contains("contract 1", exception.Message);
    }

    [Fact]
    // The records are read when another assembly is compiled; read here, they name the contract this assembly's code
    // was generated for and the middleware its closer closes, by declaring assembly and as generated code names it.
    // They are read the way code that reads build information often reads assembly metadata, into a dictionary by key,
    // which throws on a key that repeats.
    public void GeneratedCode_RecordsItsContractAndTheMiddlewareItCloses_EachUnderOneKey()
    {
        var metadata = typeof(MediatorRuntimeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(static x => x.Key, static x => x.Value);

        Assert.Equal("1", metadata["Sharp.Mediator.Contract"]);

        Assert.Contains(
            "Sharp.Mediator.Tests|global::Sharp.Mediator.Tests.Registration.MediatorServiceCollectionExtensionsTests.OpenGenericMiddleware<>",
            metadata["Sharp.Mediator.ClosedMiddleware"]!.Split(';'));
    }
}