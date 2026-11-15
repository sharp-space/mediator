using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Sharp.Mediator.Benchmarks.Scenarios;

/// <summary>
/// What an application with many messages pays before Main. A module initializer runs once per process, so each measurement is a fresh process, and the
/// assembly is loaded by name because a <c>typeof</c> anywhere in this method would run the initializer before the measurement starts. This must stay
/// flat as MessageCount grows: every message adding work here is paid by every process, whether or not it ever dispatches that message.
/// </summary>
[SimpleJob(RunStrategy.ColdStart, 15, iterationCount: 1)]
[MinColumn]
[MedianColumn]
public class StartupBenchmarks
{
    private const string ScaleAssembly = "Sharp.Mediator.ScaleFixtures";

    [SuppressMessage("Performance", "CA1822",
        Justification = "Preserve the instance benchmark shape so existing measurements remain comparable.")]
    [Benchmark]
    public void ModuleInitializer() =>
        RuntimeHelpers.RunModuleConstructor(Assembly.Load(new AssemblyName(ScaleAssembly)).ManifestModule.ModuleHandle);
}