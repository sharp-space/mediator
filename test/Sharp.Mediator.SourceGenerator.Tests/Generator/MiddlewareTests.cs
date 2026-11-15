using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using static Sharp.Mediator.SourceGenerator.Tests.Fixtures.GeneratorTestHarness;

namespace Sharp.Mediator.SourceGenerator.Tests.Generator;

// Generated-code compilability and middleware closure contracts.
public sealed class MiddlewareTests
{
    private const string PackageHeader = """
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using Sharp.Mediator;
                                         namespace Package;

                                         """;

    private const string PackageFirst = """
                                        public sealed class First<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                                        {
                                            public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                                next(message, cancellationToken);
                                        }

                                        """;

    private const string PackageSecond = """
                                         public sealed class Second<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                                         {
                                             public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                                 next(message, cancellationToken);
                                         }

                                         """;

    private const string LibrarySource = """
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using Sharp.Mediator;
                                         namespace Library;

                                         public sealed record LibraryQuery : IRequest<int>;

                                         public sealed class LibraryQueryHandler : IRequestHandler<LibraryQuery, int>
                                         {
                                             public ValueTask<int> Handle(LibraryQuery message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                         }

                                         public sealed class Own<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                                         {
                                             public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                                 next(message, cancellationToken);
                                         }
                                         """;

    private const string ApplicationSource = """
                                             using System.Threading;
                                             using System.Threading.Tasks;
                                             using Sharp.Mediator;
                                             namespace Application;

                                             public sealed record ApplicationQuery : IRequest<int>;

                                             public sealed class ApplicationQueryHandler : IRequestHandler<ApplicationQuery, int>
                                             {
                                                 public ValueTask<int> Handle(ApplicationQuery message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                             }
                                             """;
    // ---- generated-code compilability -------------------------------------------------------------------------------

    [Fact]
    public void ObsoleteAndExperimentalMessages_LeaveNoDiagnosticInGeneratedCode()
    {
        const string source = """
                              using System;
                              using System.Diagnostics.CodeAnalysis;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              [Obsolete("retired")]
                              public sealed record Retired : IRequest<int>;

                              [Experimental("TRIAL0001")]
                              public sealed record Trial : IRequest<int>;

                              // The handlers use the obsolete and experimental messages knowingly; the generated code must add no diagnostic.
                              #pragma warning disable CS0618, TRIAL0001
                              public sealed class RetiredHandler : IRequestHandler<Retired, int>
                              {
                                  public ValueTask<int> Handle(Retired message, CancellationToken cancellationToken) => new(1);
                              }

                              public sealed class TrialHandler : IRequestHandler<Trial, int>
                              {
                                  public ValueTask<int> Handle(Trial message, CancellationToken cancellationToken) => new(1);
                              }
                              """;

        AssertCompiles(RunGenerator(source));
    }

    [Fact]
    // A handler kept for a message retired with error: true is valid code as long as the handler is retired too, so the
    // registrations generated for both have to compile.
    public void ErrorLevelObsoleteMessage_LeavesNoErrorInGeneratedCode()
    {
        const string source = """
                              using System;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              [Obsolete("removed", error: true)]
                              public sealed record Removed : IRequest<int>;

                              [Obsolete("removed", error: true)]
                              public sealed class RemovedHandler : IRequestHandler<Removed, int>
                              {
                                  public ValueTask<int> Handle(Removed message, CancellationToken cancellationToken) => new(1);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        Assert.Contains("typeof(global::GeneratorFixture.Removed)", RegistrationsText(run));
    }

    [Fact]
    public void InheritanceChainBetweenHandledRequests_Compiles_BothNameOrders()
    {
        // Regression: two handled request types in an inheritance chain used to produce CS8120 (unreachable switch
        // case) because arms were emitted in name order with `is`-pattern matching. Exact-type arms make ordering
        // irrelevant; the base-type walk supplies inheritance semantics at runtime.
        const string baseSortsFirst = """
                                      using System.Threading;
                                      using System.Threading.Tasks;
                                      using Sharp.Mediator;
                                      namespace GeneratorFixture;

                                      public class BaseCommand : IRequest<int>;
                                      public sealed class DerivedCommand : BaseCommand;

                                      public sealed class BaseHandler : IRequestHandler<BaseCommand, int>
                                      {
                                          public ValueTask<int> Handle(BaseCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                      }

                                      public sealed class DerivedHandler : IRequestHandler<DerivedCommand, int>
                                      {
                                          public ValueTask<int> Handle(DerivedCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(2);
                                      }
                                      """;

        const string derivedSortsFirst = """
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using Sharp.Mediator;
                                         namespace GeneratorFixture;

                                         public class ZebraCommand : IRequest<int>;
                                         public sealed class AlphaCommand : ZebraCommand;

                                         public sealed class ZebraHandler : IRequestHandler<ZebraCommand, int>
                                         {
                                             public ValueTask<int> Handle(ZebraCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                         }

                                         public sealed class AlphaHandler : IRequestHandler<AlphaCommand, int>
                                         {
                                             public ValueTask<int> Handle(AlphaCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(2);
                                         }
                                         """;

        AssertCompiles(RunGenerator(baseSortsFirst));
        AssertCompiles(RunGenerator(derivedSortsFirst));
    }

    [Fact]
    public void InheritanceChainBetweenHandledEvents_Compiles()
    {
        // Regression: events hit the same inheritance-chain failure as requests.
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public class AlphaEvent : IEvent;
                              public sealed class BetaEvent : AlphaEvent;

                              public sealed class AlphaHandler : IEventHandler<AlphaEvent>
                              {
                                  public ValueTask Handle(AlphaEvent message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                              }

                              public sealed class BetaHandler : IEventHandler<BetaEvent>
                              {
                                  public ValueTask Handle(BetaEvent message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                              }
                              """;

        AssertCompiles(RunGenerator(source));
    }

    [Fact]
    public void MiddlewareWithExtraMessageConstraint_ClosesOnlyOverSatisfyingMessages()
    {
        // Regression: an extra constraint on the message type parameter used to be ignored, emitting a closed
        // registration that failed to compile (CS0311). Now the non-satisfying message is skipped but still marked
        // considered, so the runtime gap pass will not re-close it either.
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public interface IAuditable;

                              public sealed record Audited : IRequest, IAuditable;
                              public sealed record Plain : IRequest;

                              public sealed class AuditedHandler : IRequestHandler<Audited>
                              {
                                  public ValueTask Handle(Audited message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                              }

                              public sealed class PlainHandler : IRequestHandler<Plain>
                              {
                                  public ValueTask Handle(Plain message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                              }

                              public sealed class AuditMiddleware<TMessage> : IMiddleware<TMessage> where TMessage : IRequest, IAuditable
                              {
                                  public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        Assert.Contains(ClosedOver("global::GeneratorFixture.AuditMiddleware<global::GeneratorFixture.Audited>"), registrations);
        Assert.DoesNotContain("AuditMiddleware<global::GeneratorFixture.Plain>", registrations);

        Assert.Contains("MarkMiddlewareConsidered(middlewareType, typeof(global::Sharp.Mediator.IMiddleware<global::GeneratorFixture.Plain>))",
            registrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // A constraint can name a variant interface that a message or response meets only through variance: List<string> is
    // an IEnumerable<object> through IEnumerable<out T>, and an ISink<object> is an ISink<string> through ISink<in T>.
    // Matching names missed both and left out middleware that C# accepts, without a diagnostic. The compiler now
    // decides those pairs, so variance over a value type, a numeric conversion, or a user-defined one still leaves the
    // middleware out. A project that treats warnings as errors gets the same pairs: the compiler once judged them where
    // a nullable warning on each probe field became an error, and every pair was left out.
    public void MiddlewareWithAVariantConstraint_ClosesOverWhatMeetsItThroughVariance(bool warningsAsErrors)
    {
        const string source = """
                              using System;
                              using System.Collections.Generic;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public interface ISink<in T>;

                              public sealed class Wrapped
                              {
                                  public static implicit operator List<object>(Wrapped value) => [];
                              }

                              public sealed record Names : IRequest<List<string>>;
                              public sealed record Numbers : IRequest<List<int>>;
                              public sealed record Wrap : IRequest<Wrapped>;
                              public sealed record Sink : IRequest<int>, ISink<object>;
                              public sealed record Count : IRequest<int>;

                              public sealed class NamesHandler : IRequestHandler<Names, List<string>>
                              {
                                  public ValueTask<List<string>> Handle(Names message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<string>());
                              }

                              public sealed class NumbersHandler : IRequestHandler<Numbers, List<int>>
                              {
                                  public ValueTask<List<int>> Handle(Numbers message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<int>());
                              }

                              public sealed class WrapHandler : IRequestHandler<Wrap, Wrapped>
                              {
                                  public ValueTask<Wrapped> Handle(Wrap message, CancellationToken cancellationToken) => ValueTask.FromResult(new Wrapped());
                              }

                              public sealed class SinkHandler : IRequestHandler<Sink, int>
                              {
                                  public ValueTask<int> Handle(Sink message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class CountHandler : IRequestHandler<Count, int>
                              {
                                  public ValueTask<int> Handle(Count message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class Covariant<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : IEnumerable<object>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }

                              public sealed class Contravariant<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>, ISink<string>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }

                              public sealed class Comparing<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : IComparable<long>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source, generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default);
        AssertCompiles(run);
        Assert.DoesNotContain(run.Diagnostics, static x => x.Severity >= DiagnosticSeverity.Warning);

        var registrations = RegistrationsText(run);

        string Closed(string middleware, string message, string response) =>
            ClosedOver($"global::GeneratorFixture.{middleware}<global::GeneratorFixture.{message}, {response}>");

        Assert.Contains(Closed("Covariant", "Names", "global::System.Collections.Generic.List<string>"), registrations);
        Assert.Contains(Closed("Contravariant", "Sink", "int"), registrations);
        Assert.DoesNotContain(Closed("Covariant", "Numbers", "global::System.Collections.Generic.List<int>"), registrations);
        Assert.DoesNotContain(Closed("Covariant", "Wrap", "global::GeneratorFixture.Wrapped"), registrations);
        Assert.DoesNotContain(Closed("Contravariant", "Count", "int"), registrations);
        Assert.DoesNotContain(Closed("Comparing", "Count", "int"), registrations);

        Assert.Contains(
            "MarkMiddlewareConsidered(middlewareType, typeof(global::Sharp.Mediator.IMiddleware<global::GeneratorFixture.Count, int>))",
            registrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // Variance applies to every middleware shape: the one-parameter middleware of events and void requests, stream
    // middleware, and request middleware, each closed over the messages of its own shape alone, whether or not the
    // project treats warnings as errors.
    public void MiddlewareOfEachShape_WithAVariantConstraint_ClosesThroughVariance(bool warningsAsErrors)
    {
        const string source = """
                              using System.Collections.Generic;
                              using System.Linq;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public interface ISink<in T>;

                              public sealed record Signal : IEvent, ISink<object>;
                              public sealed record Ping : IRequest, ISink<object>;
                              public sealed record Plain : IRequest;
                              public sealed record Query : IRequest<List<string>>;
                              public sealed record Lines : IStreamRequest<List<string>>;
                              public sealed record Numbers : IStreamRequest<int>;

                              public sealed class SignalHandler : IEventHandler<Signal>
                              {
                                  public ValueTask Handle(Signal message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                              }

                              public sealed class PingHandler : IRequestHandler<Ping>
                              {
                                  public ValueTask Handle(Ping message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                              }

                              public sealed class PlainHandler : IRequestHandler<Plain>
                              {
                                  public ValueTask Handle(Plain message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                              }

                              public sealed class QueryHandler : IRequestHandler<Query, List<string>>
                              {
                                  public ValueTask<List<string>> Handle(Query message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<string>());
                              }

                              public sealed class LinesHandler : IStreamRequestHandler<Lines, List<string>>
                              {
                                  public IAsyncEnumerable<List<string>> Handle(Lines message, CancellationToken cancellationToken) => AsyncEnumerable.Empty<List<string>>();
                              }

                              public sealed class NumbersHandler : IStreamRequestHandler<Numbers, int>
                              {
                                  public IAsyncEnumerable<int> Handle(Numbers message, CancellationToken cancellationToken) => AsyncEnumerable.Empty<int>();
                              }

                              public sealed class Tagged<TMessage> : IMiddleware<TMessage> where TMessage : IMessage, ISink<string>
                              {
                                  public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }

                              public sealed class Listed<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : IEnumerable<object>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }

                              public sealed class Batched<TRequest, TResponse> : IStreamMiddleware<TRequest, TResponse>
                                  where TRequest : IStreamRequest<TResponse>
                                  where TResponse : IEnumerable<object>
                              {
                                  public IAsyncEnumerable<TResponse> Handle(
                                      TRequest message, StreamMessageHandle<TRequest, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source, generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        const string list = "global::System.Collections.Generic.List<string>";

        Assert.Contains(ClosedOver("global::GeneratorFixture.Tagged<global::GeneratorFixture.Signal>"), registrations);
        Assert.Contains(ClosedOver("global::GeneratorFixture.Tagged<global::GeneratorFixture.Ping>"), registrations);
        Assert.Contains(ClosedOver($"global::GeneratorFixture.Listed<global::GeneratorFixture.Query, {list}>"), registrations);
        Assert.Contains(ClosedOver($"global::GeneratorFixture.Batched<global::GeneratorFixture.Lines, {list}>"), registrations);
        Assert.DoesNotContain("Tagged<global::GeneratorFixture.Plain>", registrations);
        Assert.DoesNotContain("Batched<global::GeneratorFixture.Numbers", registrations);
    }

    [Fact]
    // The compiler judges the pairs in the context it compiles the generated code in, which is an obsolete context with
    // every warning off. Judged anywhere else, a message, response or middleware marked [Obsolete(..., error: true)],
    // or one marked [Experimental] whose diagnostic a #pragma turns off where it is used, reported an error and left
    // the middleware out without a diagnostic. A pair that variance does not satisfy stays out all the same.
    public void MiddlewareWithAVariantConstraint_ClosesOverObsoleteAndExperimentalTypes()
    {
        const string source = """
                              // The fixture uses its experimental types knowingly, as the pragma case in the comment above describes.
                              #pragma warning disable EXP001, EXP002, EXP003
                              using System;
                              using System.Collections.Generic;
                              using System.Diagnostics.CodeAnalysis;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed record Names : IRequest<List<string>>;

                              [Obsolete("Retired.", error: true)]
                              public sealed record OldNames : IRequest<List<string>>;

                              [Obsolete("Retired.", error: true)]
                              public sealed class OldList : List<string>;

                              [Obsolete("Answers with a retired response.")]
                              public sealed record OldResponse : IRequest<OldList>;

                              [Experimental("EXP001")]
                              public sealed record Trial : IRequest<List<string>>;

                              [Experimental("EXP002")]
                              public sealed class TrialList : List<string>;

                              public sealed record TrialResponse : IRequest<TrialList>;

                              [Obsolete("Retired.", error: true)]
                              public sealed record OldNumbers : IRequest<List<int>>;

                              public sealed class NamesHandler : IRequestHandler<Names, List<string>>
                              {
                                  public ValueTask<List<string>> Handle(Names message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<string>());
                              }

                              [Obsolete("Handles a retired message.")]
                              public sealed class OldNamesHandler : IRequestHandler<OldNames, List<string>>
                              {
                                  public ValueTask<List<string>> Handle(OldNames message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<string>());
                              }

                              [Obsolete("Handles a retired response.")]
                              public sealed class OldResponseHandler : IRequestHandler<OldResponse, OldList>
                              {
                                  public ValueTask<OldList> Handle(OldResponse message, CancellationToken cancellationToken) => ValueTask.FromResult(new OldList());
                              }

                              public sealed class TrialHandler : IRequestHandler<Trial, List<string>>
                              {
                                  public ValueTask<List<string>> Handle(Trial message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<string>());
                              }

                              public sealed class TrialResponseHandler : IRequestHandler<TrialResponse, TrialList>
                              {
                                  public ValueTask<TrialList> Handle(TrialResponse message, CancellationToken cancellationToken) => ValueTask.FromResult(new TrialList());
                              }

                              [Obsolete("Handles a retired message.")]
                              public sealed class OldNumbersHandler : IRequestHandler<OldNumbers, List<int>>
                              {
                                  public ValueTask<List<int>> Handle(OldNumbers message, CancellationToken cancellationToken) => ValueTask.FromResult(new List<int>());
                              }

                              public sealed class Covariant<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : IEnumerable<object>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }

                              [Obsolete("Retired.", error: true)]
                              public sealed class OldCovariant<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : IEnumerable<object>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }

                              [Experimental("EXP003")]
                              public sealed class TrialCovariant<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : IEnumerable<object>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        const string list = "global::System.Collections.Generic.List<string>";

        string Closed(string middleware, string message, string response) =>
            ClosedOver($"global::GeneratorFixture.{middleware}<global::GeneratorFixture.{message}, {response}>");

        Assert.Contains(Closed("Covariant", "OldNames", list), registrations);
        Assert.Contains(Closed("Covariant", "OldResponse", "global::GeneratorFixture.OldList"), registrations);
        Assert.Contains(Closed("Covariant", "Trial", list), registrations);
        Assert.Contains(Closed("Covariant", "TrialResponse", "global::GeneratorFixture.TrialList"), registrations);
        Assert.Contains(Closed("OldCovariant", "Names", list), registrations);
        Assert.Contains(Closed("TrialCovariant", "Names", list), registrations);
        Assert.DoesNotContain(Closed("Covariant", "OldNumbers", "global::System.Collections.Generic.List<int>"), registrations);
        Assert.DoesNotContain(Closed("OldCovariant", "OldNumbers", "global::System.Collections.Generic.List<int>"), registrations);
        Assert.DoesNotContain(Closed("TrialCovariant", "OldNumbers", "global::System.Collections.Generic.List<int>"), registrations);
    }

    [Fact]
    public void MiddlewareWithStructResponseConstraint_SkipsClassResponses()
    {
        // Regression: `where TResponse : struct` was folded into "needs new()", so a class response with a
        // parameterless constructor produced an uncompilable closed registration (CS0453).
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed class Payload { }

                              public sealed record GetPayload : IRequest<Payload>;
                              public sealed record GetNumber : IRequest<int>;

                              public sealed class GetPayloadHandler : IRequestHandler<GetPayload, Payload>
                              {
                                  public ValueTask<Payload> Handle(GetPayload message, CancellationToken cancellationToken) => ValueTask.FromResult(new Payload());
                              }

                              public sealed class GetNumberHandler : IRequestHandler<GetNumber, int>
                              {
                                  public ValueTask<int> Handle(GetNumber message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class StructOnlyMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse> where TResponse : struct
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        Assert.Contains("StructOnlyMiddleware<global::GeneratorFixture.GetNumber, int>", registrations);
        Assert.DoesNotContain("StructOnlyMiddleware<global::GeneratorFixture.GetPayload, global::GeneratorFixture.Payload>", registrations);
    }

    [Fact]
    public void StandardSelfReferentialConstraint_StillCloses_WithoutSmd010()
    {
        // Regression: the canonical middleware shape, `where TMessage : IMessage<TResponse>`, references a sibling type
        // parameter and must be checked by substitution rather than treated as unclosable.
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed record Ping : IRequest<int>;

                              public sealed class PingHandler : IRequestHandler<Ping, int>
                              {
                                  public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class LoggingMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        Assert.DoesNotContain(run.Diagnostics, static x => x.Id == "SMD010");
        AssertCompiles(run);
        Assert.Contains("LoggingMiddleware<global::GeneratorFixture.Ping, int>", RegistrationsText(run));
    }

    [Fact]
    public void OpenGenericImplementingRequestAndStreamMiddleware_EmitsBothInOneArm()
    {
        // Regression: per-interface middleware infos used to be deduped by match type, silently dropping every
        // interface but the first. Both interfaces must be evaluated in the single arm.
        const string source = """
                              using System.Collections.Generic;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed record Count : IStreamRequest<int>;

                              public sealed class CountHandler : IStreamRequestHandler<Count, int>
                              {
                                  public async IAsyncEnumerable<int> Handle(Count message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                                  {
                                      yield return 1;
                                  }
                              }

                              public sealed record Ping : IRequest<int>;

                              public sealed class PingHandler : IRequestHandler<Ping, int>
                              {
                                  public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class WideMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>, IStreamMiddleware<TMessage, TResponse>
                                  where TMessage : IStreamRequest<TResponse>, IRequest<TResponse>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);

                                  public IAsyncEnumerable<TResponse> Handle(TMessage message, StreamMessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        var armCount = CountOccurrences(registrations, "if (middlewareType == typeof(global::GeneratorFixture.WideMiddleware<,>))");
        Assert.Equal(1, armCount);

        // Neither message satisfies both constraints, so nothing closes — but both interfaces' pairs are considered.
        Assert.Contains("MarkMiddlewareConsidered(middlewareType, typeof(global::Sharp.Mediator.IMiddleware<global::GeneratorFixture.Ping, int>))",
            registrations);

        Assert.Contains(
            "MarkMiddlewareConsidered(middlewareType, typeof(global::Sharp.Mediator.IStreamMiddleware<global::GeneratorFixture.Count, int>))",
            registrations);
    }

    [Fact]
    public void PublicMiddlewareInReferencedAssembly_ClosesOverLocalMessages_WithoutRegistrationMetadata()
    {
        // The cross-assembly path: a middleware package referenced as metadata, built without the generator and
        // declaring nothing but its middleware. Scanning its public types is enough for this compilation to close the
        // package's open generics over its own messages.
        const string middlewarePackageSource = """
                                               using System.Collections.Generic;
                                               using System.Threading;
                                               using System.Threading.Tasks;
                                               using Sharp.Mediator;
                                               namespace MiddlewarePackage;

                                               public sealed class PackagedMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                                   where TMessage : IMessage<TResponse>
                                               {
                                                   public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                                       next(message, cancellationToken);
                                               }

                                               public static class Outer
                                               {
                                                   public sealed class NestedMiddleware<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
                                                   {
                                                       public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                                                           next(message, cancellationToken);
                                                   }
                                               }

                                               public sealed class PackagedStreamMiddleware<TRequest, TResponse> : IStreamMiddleware<TRequest, TResponse>
                                                   where TRequest : IStreamRequest<TResponse>
                                               {
                                                   public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamMessageHandle<TRequest, TResponse> next, CancellationToken cancellationToken) =>
                                                       next(request, cancellationToken);
                                               }
                                               """;

        const string handlerSource = """
                                     using System.Collections.Generic;
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;
                                     namespace GeneratorFixture;

                                     public sealed record Ping : IRequest<int>;
                                     public sealed record Notify : IRequest;
                                     public sealed record Count : IStreamRequest<int>;

                                     public sealed class PingHandler : IRequestHandler<Ping, int>
                                     {
                                         public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }

                                     public sealed class NotifyHandler : IRequestHandler<Notify>
                                     {
                                         public ValueTask Handle(Notify message, CancellationToken cancellationToken) => default;
                                     }

                                     public sealed class CountHandler : IStreamRequestHandler<Count, int>
                                     {
                                         public async IAsyncEnumerable<int> Handle(Count request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                                         {
                                             yield return 1;
                                             await Task.Yield();
                                         }
                                     }
                                     """;

        var run = RunGenerator(handlerSource, [CompileToReference("MiddlewarePackage", middlewarePackageSource)]);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        Assert.Contains("PackagedMiddleware<global::GeneratorFixture.Ping, int>", registrations);
        Assert.Contains("Outer.NestedMiddleware<global::GeneratorFixture.Notify>", registrations);
        Assert.Contains("PackagedStreamMiddleware<global::GeneratorFixture.Count, int>", registrations);
    }

    [Fact]
    public void ReferencedTypesThatAreNotCloseableMiddleware_AreSkippedWithoutDiagnostics()
    {
        // Every type of a referenced assembly is visited, so what is not a closeable middleware must stay out quietly:
        // the declaring assembly diagnoses its own middleware, and this one can do nothing about it.
        const string packageSource = """
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;
                                     namespace Package;

                                     public struct NotAClass;
                                     public interface INotAClass;
                                     public enum NotAType { Value }
                                     public delegate void NotAMiddleware();
                                     public sealed class GenericUtility<T>;

                                     public abstract class AbstractMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                         where TMessage : IMessage<TResponse>
                                     {
                                         public abstract ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken);
                                     }

                                     public sealed class FixedResponseMiddleware<TMessage> : IMiddleware<TMessage, int> where TMessage : IMessage<int>
                                     {
                                         public ValueTask<int> Handle(TMessage message, MessageHandle<TMessage, int> next, CancellationToken cancellationToken) =>
                                             next(message, cancellationToken);
                                     }

                                     public sealed class GenericContainer<T>
                                     {
                                         public sealed class NestedMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                             where TMessage : IMessage<TResponse>
                                         {
                                             public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                                 next(message, cancellationToken);
                                         }
                                     }

                                     public struct StructContainer
                                     {
                                         private sealed class PrivateMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                             where TMessage : IMessage<TResponse>
                                         {
                                             public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                                 next(message, cancellationToken);
                                         }
                                     }
                                     """;

        const string handlerSource = """
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;
                                     namespace GeneratorFixture;

                                     public sealed record Ping : IRequest<int>;

                                     public sealed class PingHandler : IRequestHandler<Ping, int>
                                     {
                                         public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }
                                     """;

        var run = RunGenerator(handlerSource, [CompileToReference("Package", packageSource)]);
        AssertCompiles(run);
        Assert.Empty(run.Diagnostics);
        Assert.DoesNotContain("CloseMiddleware", RegistrationsText(run));
    }

    [Fact]
    public void MiddlewareInAnAssemblyReferencedThroughAnExternAlias_IsNotClosed()
    {
        // Generated code names types with global::, which does not reach an assembly referenced only through an alias.
        const string packageSource = """
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;
                                     namespace Package;

                                     public sealed class AliasedMiddleware<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                         where TMessage : IMessage<TResponse>
                                     {
                                         public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                             next(message, cancellationToken);
                                     }
                                     """;

        const string handlerSource = """
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;
                                     namespace GeneratorFixture;

                                     public sealed record Ping : IRequest<int>;

                                     public sealed class PingHandler : IRequestHandler<Ping, int>
                                     {
                                         public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }
                                     """;

        var aliased = CompileToReference("AliasedPackage", packageSource).WithAliases(["package"]);
        var run = RunGenerator(handlerSource, [aliased]);
        AssertCompiles(run);
        Assert.DoesNotContain("AliasedMiddleware", RegistrationsText(run));
    }

    // ---- messages of referenced assemblies --------------------------------------------------------------------------

    [Fact]
    public void LocalMiddleware_ClosesOverPublicMessagesOfReferencedHandlerAssemblies()
    {
        // An application that declares middleware but no handlers, over a handler library that cannot see that
        // middleware: only the application can close the pairs, so its closer covers the messages the library
        // dispatches and generated code can name.
        const string librarySource = """
                                     using System.Collections.Generic;
                                     using System.Runtime.CompilerServices;
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;
                                     namespace Library;

                                     public sealed record Ping : IRequest<int>;
                                     public sealed record Notify : IRequest;
                                     public sealed record Happened : IEvent, IHappened;
                                     public interface IHappened : IEvent;
                                     public sealed record Count : IStreamRequest<int>;
                                     internal sealed record Secret : IRequest<int>;
                                     public sealed record Both : IRequest, IRequest<int>;
                                     public sealed record Twice : IRequest<int>, IRequest<string>;
                                     public interface IAsk : IRequest<int>;

                                     internal sealed class PingHandler : IRequestHandler<Ping, int>
                                     {
                                         public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }

                                     public static class Handlers
                                     {
                                         public sealed class NotifyHandler : IRequestHandler<Notify>
                                         {
                                             public ValueTask Handle(Notify message, CancellationToken cancellationToken) => default;
                                         }
                                     }

                                     public sealed class HappenedHandler : IEventHandler<Happened>, IEventHandler<IHappened>
                                     {
                                         public ValueTask Handle(Happened message, CancellationToken cancellationToken) => default;
                                         public ValueTask Handle(IHappened message, CancellationToken cancellationToken) => default;
                                     }

                                     public sealed class CountHandler : IStreamRequestHandler<Count, int>
                                     {
                                         public async IAsyncEnumerable<int> Handle(Count request, [EnumeratorCancellation] CancellationToken cancellationToken)
                                         {
                                             yield return 1;
                                             await Task.Yield();
                                         }
                                     }

                                     internal sealed class SecretHandler : IRequestHandler<Secret, int>
                                     {
                                         public ValueTask<int> Handle(Secret message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }

                                     public sealed class BothHandler : IRequestHandler<Both, int>
                                     {
                                         public ValueTask<int> Handle(Both message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }

                                     public sealed class TwiceHandler : IRequestHandler<Twice, int>
                                     {
                                         public ValueTask<int> Handle(Twice message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }

                                     public sealed class AskHandler : IRequestHandler<IAsk, int>
                                     {
                                         public ValueTask<int> Handle(IAsk message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }

                                     public abstract class AbstractHandler : IRequestHandler<Ping, int>
                                     {
                                         public abstract ValueTask<int> Handle(Ping message, CancellationToken cancellationToken);
                                     }

                                     public sealed class Container<T>
                                     {
                                         public sealed class NestedHandler : IRequestHandler<Ping, int>
                                         {
                                             public ValueTask<int> Handle(Ping message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                         }
                                     }
                                     """;

        const string applicationSource = """
                                         using System.Collections.Generic;
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using Sharp.Mediator;
                                         namespace Application;

                                         public sealed class Trace<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                                         {
                                             public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                                 next(message, cancellationToken);
                                         }

                                         public sealed class VoidTrace<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
                                         {
                                             public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                                                 next(message, cancellationToken);
                                         }

                                         public sealed class StreamTrace<TRequest, TResponse> : IStreamMiddleware<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
                                         {
                                             public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamMessageHandle<TRequest, TResponse> next, CancellationToken cancellationToken) =>
                                                 next(request, cancellationToken);
                                         }
                                         """;

        var run = RunGenerator(applicationSource, [CompileToReference("Library", librarySource)], "Application");
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        Assert.Contains("Trace<global::Library.Ping, int>", registrations);
        Assert.Contains("VoidTrace<global::Library.Notify>", registrations);
        Assert.Contains("VoidTrace<global::Library.Happened>", registrations);
        Assert.Contains("VoidTrace<global::Library.IHappened>", registrations);
        Assert.Contains("StreamTrace<global::Library.Count, int>", registrations);
        Assert.Equal(1, CountOccurrences(registrations, ClosedOver("global::Application.Trace<global::Library.Ping, int>")));

        // Generated code cannot name an internal message, and the library's own generator rejects the rest.
        Assert.DoesNotContain("Secret", registrations);
        Assert.DoesNotContain("Both", registrations);
        Assert.DoesNotContain("Twice", registrations);
        Assert.DoesNotContain("IAsk", registrations);

        // The application dispatches none of these messages itself: they stay out of its routes and handler services.
        Assert.DoesNotContain("CreateRequestRoute", registrations);
        Assert.DoesNotContain("IRequestHandler<global::Library.Ping, int>", registrations);
    }

    [Fact]
    public void ReferencedMiddleware_IsClosedOverReferencedMessages_OnlyWhereTheHandlerAssemblyCouldNot()
    {
        // A handler assembly's own generator closes its own middleware and the public middleware of what it references,
        // so this compilation closes only the pairs no generator saw together.
        const string contractsSource = """
                                       using Sharp.Mediator;
                                       namespace Contracts;

                                       public sealed record Shout : IEvent;
                                       """;

        const string packageSource = """
                                     using System.Runtime.CompilerServices;
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;
                                     [assembly: InternalsVisibleTo("Application")]
                                     namespace Package;

                                     public sealed class Packaged<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
                                     {
                                         public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                                             next(message, cancellationToken);
                                     }

                                     internal sealed class Shared<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
                                     {
                                         public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                                             next(message, cancellationToken);
                                     }
                                     """;

        const string seeingSource = """
                                    using System.Threading;
                                    using System.Threading.Tasks;
                                    using Sharp.Mediator;
                                    namespace Seeing;

                                    public sealed record Seen : IRequest;

                                    public sealed class SeenHandler : IRequestHandler<Seen>
                                    {
                                        public ValueTask Handle(Seen message, CancellationToken cancellationToken) => default;
                                    }

                                    public sealed class ShoutHandler : IEventHandler<Contracts.Shout>
                                    {
                                        public ValueTask Handle(Contracts.Shout message, CancellationToken cancellationToken) => default;
                                    }

                                    public sealed class Own<TMessage> : IMiddleware<TMessage> where TMessage : IMessage
                                    {
                                        public ValueTask Handle(TMessage message, MessageHandle<TMessage> next, CancellationToken cancellationToken) =>
                                            next(message, cancellationToken);
                                    }
                                    """;

        const string blindSource = """
                                   using System.Threading;
                                   using System.Threading.Tasks;
                                   using Sharp.Mediator;
                                   namespace Blind;

                                   public sealed record Hidden : IRequest;
                                   public sealed record Unseen : IRequest;

                                   public sealed class HiddenHandler : IRequestHandler<Hidden>
                                   {
                                       public ValueTask Handle(Hidden message, CancellationToken cancellationToken) => default;
                                   }

                                   public sealed class UnseenHandler : IRequestHandler<Unseen>
                                   {
                                       public ValueTask Handle(Unseen message, CancellationToken cancellationToken) => default;
                                   }

                                   public sealed class ShoutHandler : IEventHandler<Contracts.Shout>
                                   {
                                       public ValueTask Handle(Contracts.Shout message, CancellationToken cancellationToken) => default;
                                   }
                                   """;

        const string applicationSource = """
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using Sharp.Mediator;
                                         namespace Application;

                                         public sealed class UnseenHandler : IRequestHandler<Blind.Unseen>
                                         {
                                             public ValueTask Handle(Blind.Unseen message, CancellationToken cancellationToken) => default;
                                         }
                                         """;

        var contracts = CompileToReference("Contracts", contractsSource);
        var package = CompileToReference("Package", packageSource);
        var seeing = CompileGeneratedToReference("Seeing", seeingSource, [package, contracts]);
        var blind = CompileGeneratedToReference("Blind", blindSource, [contracts]);
        var run = RunGenerator(applicationSource, [contracts, package, seeing, blind], "Application");
        AssertCompiles(run);

        var registrations = RegistrationsText(run);

        int Closes(string middleware, string message) =>
            CountOccurrences(registrations, ClosedOver($"{middleware}<{message}>"));

        // Seeing references the package, so its generator closed the public middleware, and its own, over its messages.
        // It cannot name the internal one, which only this compilation can.
        Assert.Equal(0, Closes("global::Package.Packaged", "global::Seeing.Seen"));
        Assert.Equal(1, Closes("global::Package.Shared", "global::Seeing.Seen"));
        Assert.Equal(0, Closes("global::Seeing.Own", "global::Seeing.Seen"));

        // Nothing but this compilation sees Blind together with any middleware.
        Assert.Equal(1, Closes("global::Package.Packaged", "global::Blind.Hidden"));
        Assert.Equal(1, Closes("global::Package.Shared", "global::Blind.Hidden"));
        Assert.Equal(1, Closes("global::Seeing.Own", "global::Blind.Hidden"));

        // An event handled in both is closed over once, and not where one handler's assembly already closed it.
        Assert.Equal(0, Closes("global::Package.Packaged", "global::Contracts.Shout"));
        Assert.Equal(1, Closes("global::Package.Shared", "global::Contracts.Shout"));
        Assert.Equal(0, Closes("global::Seeing.Own", "global::Contracts.Shout"));

        // A referenced message with a handler here is one of this compilation's own, closed over once.
        Assert.Equal(1, Closes("global::Package.Packaged", "global::Blind.Unseen"));
        Assert.Equal(1, Closes("global::Seeing.Own", "global::Blind.Unseen"));
    }

    [Fact]
    // Generated code records, as assembly metadata, the contract it was generated for and, in one value, the middleware
    // its closer closes over the assembly's messages, its own and those of the assemblies it references, for the
    // generator of an assembly that references this one to read. Each key once: code that reads an assembly's metadata
    // into a dictionary by key throws on a key that repeats.
    public void Registrar_RecordsItsContractAndTheMiddlewareItsCloserCloses_EachUnderOneKey()
    {
        var run = RunGenerator(
            LibrarySource,
            [CompileToReference("Package", PackageHeader + PackageFirst + PackageSecond)], "Library");

        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        const string metadata = "[assembly: global::System.Reflection.AssemblyMetadata(";
        Assert.Contains(metadata + "\"Sharp.Mediator.Contract\", \"1\")]", registrations);

        Assert.Contains(
            metadata + "\"Sharp.Mediator.ClosedMiddleware\", " +
            "\"Library|global::Library.Own<,>;Package|global::Package.First<,>;Package|global::Package.Second<,>\")]",
            registrations);

        Assert.Equal(1, CountOccurrences(registrations, "\"Sharp.Mediator.Contract\""));
        Assert.Equal(1, CountOccurrences(registrations, "\"Sharp.Mediator.ClosedMiddleware\""));
    }

    [Fact]
    // Only assembly metadata under the record's key is a record. A handler assembly built without the generator records
    // nothing, so another attribute that carries the same strings, here one named like the metadata attribute, leaves
    // the application closing the middleware over its message.
    public void AttributeThatOnlyLooksLikeARecord_ClosesNothingForItsAssembly()
    {
        const string lookalikeSource = """
                                       using System;
                                       using System.Threading;
                                       using System.Threading.Tasks;
                                       using Sharp.Mediator;

                                       [assembly: Library.AssemblyMetadata("Sharp.Mediator.ClosedMiddleware", "Package|global::Package.First<,>")]

                                       namespace Library;

                                       [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                                       public sealed class AssemblyMetadataAttribute(string key, string value) : Attribute
                                       {
                                           public string Key { get; } = key;
                                           public string Value { get; } = value;
                                       }

                                       public sealed record LibraryQuery : IRequest<int>;

                                       public sealed class LibraryQueryHandler : IRequestHandler<LibraryQuery, int>
                                       {
                                           public ValueTask<int> Handle(LibraryQuery message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                       }
                                       """;

        var package = CompileToReference("Package", PackageHeader + PackageFirst);
        var run = RunGenerator(ApplicationSource, [package, CompileToReference("Library", lookalikeSource)], "Application");
        AssertCompiles(run);

        Assert.Equal(1, CountOccurrences(
            RegistrationsText(run), ClosedOver("global::Package.First<global::Library.LibraryQuery, int>")));
    }

    [Fact]
    // Metadata under the record's key that holds no list records nothing, so the application closes the middleware over
    // the assembly's message: a null value, or arguments that bind to no constructor, as in a referenced project that
    // does not compile, whose attribute then has no arguments at all.
    public void MetadataUnderTheRecordKeyThatHoldsNoList_ClosesNothingForItsAssembly()
    {
        const string librarySource = """
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Sharp.Mediator;

                                     [assembly: System.Reflection.AssemblyMetadata("Sharp.Mediator.ClosedMiddleware", null)]
                                     [assembly: System.Reflection.AssemblyMetadata("Sharp.Mediator.ClosedMiddleware")]

                                     namespace Library;

                                     public sealed record LibraryQuery : IRequest<int>;

                                     public sealed class LibraryQueryHandler : IRequestHandler<LibraryQuery, int>
                                     {
                                         public ValueTask<int> Handle(LibraryQuery message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                     }
                                     """;

        var library = CSharpCompilation.Create(
            "Library", [CSharpSyntaxTree.ParseText(librarySource, new CSharpParseOptions(LanguageVersion.Preview))],
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Contains(library.GetDiagnostics(), static x => x.Id == "CS7036");

        var run = RunGenerator(
            ApplicationSource,
            [CompileToReference("Package", PackageHeader + PackageFirst), library.ToMetadataReference()], "Application");

        Assert.DoesNotContain(run.Diagnostics, static x => x.Id == "CS8785");
        AssertCompiles(run);

        Assert.Equal(1, CountOccurrences(
            RegistrationsText(run), ClosedOver("global::Package.First<global::Library.LibraryQuery, int>")));
    }

    [Fact]
    // A handler assembly built against a package that later gained a middleware: the generator took the assembly's
    // reference to the package for proof that the assembly's generator had closed every public middleware of the
    // package over its messages, so no generator closed the new one, which the JIT then closed by reflection at
    // startup and Native AOT refused to start over. It reads the assembly's record of what its closer closes now, and
    // closes the rest.
    public void MiddlewareAddedToAPackageAfterAHandlerAssemblyWasBuilt_IsClosedByTheApplication()
    {
        var library = CompileGeneratedToReference(
            "Library", LibrarySource, [CompileToReference("Package", PackageHeader + PackageFirst)]);

        var run = RunGenerator(
            ApplicationSource, [CompileToReference("Package", PackageHeader + PackageFirst + PackageSecond), library],
            "Application");

        AssertCompiles(run);

        var registrations = RegistrationsText(run);

        int Closes(string middleware, string message) =>
            CountOccurrences(registrations, ClosedOver($"{middleware}<{message}, int>"));

        Assert.Equal(0, Closes("global::Package.First", "global::Library.LibraryQuery"));
        Assert.Equal(0, Closes("global::Library.Own", "global::Library.LibraryQuery"));
        Assert.Equal(1, Closes("global::Package.Second", "global::Library.LibraryQuery"));
        Assert.Equal(1, Closes("global::Package.First", "global::Application.ApplicationQuery"));
        Assert.Equal(1, Closes("global::Package.Second", "global::Application.ApplicationQuery"));
    }

    // ---- nullable responses -----------------------------------------------------------------------------------------

    [Fact]
    public void NullableReferenceResponses_GenerateWarningFreeCode()
    {
        // Regression: type names lose their `?`, so generated code saw `GetUser` used as `IRequest<User>` and reported
        // CS8631 at every closed generic, failing TreatWarningsAsErrors builds.
        const string source = """
                              using System.Collections.Generic;
                              using System.Runtime.CompilerServices;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed record User(string Name);
                              public sealed record GetUser : IRequest<User?>;
                              public sealed record Tail : IStreamRequest<string?>;

                              public sealed class GetUserHandler : IRequestHandler<GetUser, User?>
                              {
                                  public ValueTask<User?> Handle(GetUser message, CancellationToken cancellationToken) => ValueTask.FromResult<User?>(null);
                              }

                              public sealed class TailHandler : IStreamRequestHandler<Tail, string?>
                              {
                                  public async IAsyncEnumerable<string?> Handle(Tail request, [EnumeratorCancellation] CancellationToken cancellationToken)
                                  {
                                      yield return null;
                                      await Task.Yield();
                                  }
                              }

                              public sealed class Trace<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        AssertCompiles(RunGenerator(source));
    }

    [Fact]
    public void NotNullConstraint_WithNullableValueResponse_GeneratesWarningFreeCode()
    {
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed record Find : IRequest<int?>;

                              public sealed class FindHandler : IRequestHandler<Find, int?>
                              {
                                  public ValueTask<int?> Handle(Find message, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(1);
                              }

                              public sealed class NotNullOnly<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : notnull
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        AssertCompiles(RunGenerator(source));
    }

    // ---- constraint shapes the compiler rejects but plain facts accept ----------------------------------------------

    [Fact]
    public void StructConstraint_SkipsNullableValueResponses()
    {
        // Nullable<T> is a value type yet does not satisfy `struct` (CS0453).
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed record Nullable : IRequest<int?>;
                              public sealed record Plain : IRequest<int>;

                              public sealed class NullableHandler : IRequestHandler<Nullable, int?>
                              {
                                  public ValueTask<int?> Handle(Nullable message, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(1);
                              }

                              public sealed class PlainHandler : IRequestHandler<Plain, int>
                              {
                                  public ValueTask<int> Handle(Plain message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class ValueOnly<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : struct
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        Assert.Contains("ValueOnly<global::GeneratorFixture.Plain, int>", registrations);
        Assert.DoesNotContain("ValueOnly<global::GeneratorFixture.Nullable", registrations);
    }

    [Fact]
    public void NewConstraint_SkipsResponsesWithUnsetRequiredMembers()
    {
        // Closers are emitted for every declared or referenced open generic, so without this check a required member on
        // a response failed the build with CS9040 even if nothing was registered.
        const string source = """
                              using System.Diagnostics.CodeAnalysis;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed class RequiredProperty { public required string Name { get; init; } }
                              public sealed class RequiredField { public required string Name; }
                              public class RequiredBase { public required string Name { get; init; } }
                              public sealed class RequiredInherited : RequiredBase;
                              public struct RequiredStruct { public required string Name; }

                              public sealed class RequiredWithArgument
                              {
                                  public RequiredWithArgument(int value) => Value = value;
                                  public int Value { get; }
                                  public required string Name { get; init; }
                              }

                              public sealed class RequiredSetByConstructor
                              {
                                  [SetsRequiredMembers]
                                  public RequiredSetByConstructor() => Name = "set";
                                  public required string Name { get; init; }
                              }

                              public sealed record A : IRequest<RequiredProperty>;
                              public sealed record B : IRequest<RequiredField>;
                              public sealed record C : IRequest<RequiredInherited>;
                              public sealed record D : IRequest<RequiredStruct>;
                              public sealed record E : IRequest<RequiredWithArgument>;
                              public sealed record F : IRequest<RequiredSetByConstructor>;

                              public sealed class Handlers :
                                  IRequestHandler<A, RequiredProperty>, IRequestHandler<B, RequiredField>, IRequestHandler<C, RequiredInherited>,
                                  IRequestHandler<D, RequiredStruct>, IRequestHandler<E, RequiredWithArgument>, IRequestHandler<F, RequiredSetByConstructor>
                              {
                                  public ValueTask<RequiredProperty> Handle(A message, CancellationToken cancellationToken) => throw null!;
                                  public ValueTask<RequiredField> Handle(B message, CancellationToken cancellationToken) => throw null!;
                                  public ValueTask<RequiredInherited> Handle(C message, CancellationToken cancellationToken) => throw null!;
                                  public ValueTask<RequiredStruct> Handle(D message, CancellationToken cancellationToken) => throw null!;
                                  public ValueTask<RequiredWithArgument> Handle(E message, CancellationToken cancellationToken) => throw null!;
                                  public ValueTask<RequiredSetByConstructor> Handle(F message, CancellationToken cancellationToken) => throw null!;
                              }

                              public sealed class Creates<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>
                                  where TResponse : new()
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        Assert.Contains("Creates<global::GeneratorFixture.F, global::GeneratorFixture.RequiredSetByConstructor>", registrations);

        foreach (var message in new[] { "A", "B", "C", "D", "E" })
            Assert.DoesNotContain($"Creates<global::GeneratorFixture.{message},", registrations);
    }

    // ---- constraints that string matching used to miss --------------------------------------------------------------

    [Fact]
    public void ConstraintThroughAGenericContainer_ClosesOverSatisfyingMessages()
    {
        // The type parameter sits in the containing type's arguments, so the constraint was treated as a literal name,
        // never matched, and the pair was marked considered: the middleware silently never ran.
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public static class Outer<T>
                              {
                                  public interface IMarked;
                              }

                              public sealed record Marked : IRequest<int>, Outer<int>.IMarked;
                              public sealed record Unmarked : IRequest<int>;

                              public sealed class MarkedHandler : IRequestHandler<Marked, int>
                              {
                                  public ValueTask<int> Handle(Marked message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class UnmarkedHandler : IRequestHandler<Unmarked, int>
                              {
                                  public ValueTask<int> Handle(Unmarked message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                              }

                              public sealed class MarkedOnly<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
                                  where TMessage : IMessage<TResponse>, Outer<TResponse>.IMarked
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);

        var registrations = RegistrationsText(run);
        Assert.Contains("MarkedOnly<global::GeneratorFixture.Marked, int>", registrations);
        Assert.DoesNotContain("MarkedOnly<global::GeneratorFixture.Unmarked", registrations);
    }

    [Fact]
    public void TupleElementNames_DoNotAffectConstraintMatching()
    {
        // The message and handler may name tuple elements differently. Comparing display strings then failed
        // `TMessage : IMessage<TResponse>`, which silently removed every middleware, validation included.
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Sharp.Mediator;
                              namespace GeneratorFixture;

                              public sealed record Pair : IRequest<(int A, string B)>;

                              public sealed class PairHandler : IRequestHandler<Pair, (int X, string Y)>
                              {
                                  public ValueTask<(int X, string Y)> Handle(Pair message, CancellationToken cancellationToken) => ValueTask.FromResult((1, "a"));
                              }

                              public sealed class Trace<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                              {
                                  public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                      next(message, cancellationToken);
                              }
                              """;

        var run = RunGenerator(source);
        AssertCompiles(run);
        Assert.Contains("Trace<global::GeneratorFixture.Pair,", RegistrationsText(run));
    }

    // ---- repeated AddMediator across late-initialized assemblies ----------------------------------------------------

    [Fact]
    public async Task RepeatedAddMediator_AppliesEarlierMiddleware_ToAssembliesInitializedLater()
    {
        // A module initializer runs when the assembly is first touched, which under ReadyToRun can be after an earlier
        // AddMediator call registered the middleware. In-memory assemblies give the same ordering deterministically:
        // AddHandlers is the first access to the late module.
        const string hostSource = """
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using Sharp.Mediator;
                                  namespace HostModule;

                                  public sealed class Trace<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                                  {
                                      public static int Hits;

                                      public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
                                      {
                                          Hits++;
                                          return next(message, cancellationToken);
                                      }
                                  }

                                  public sealed record HostPing : IRequest<int>;

                                  public sealed class HostPingHandler : IRequestHandler<HostPing, int>
                                  {
                                      public ValueTask<int> Handle(HostPing message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
                                  }
                                  """;

        const string lateSource = """
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using Sharp.Mediator;
                                  namespace LateModule;

                                  public sealed record LatePing : IRequest<int>;

                                  public sealed class LatePingHandler : IRequestHandler<LatePing, int>
                                  {
                                      public ValueTask<int> Handle(LatePing message, CancellationToken cancellationToken) => ValueTask.FromResult(2);
                                  }
                                  """;

        var host = CompileGeneratedToAssembly("RepeatedAddMediatorHost", hostSource);
        var late = CompileGeneratedToAssembly("RepeatedAddMediatorLate", lateSource);
        var openMiddleware = host.GetType("HostModule.Trace`2")!;
        var latePing = late.GetType("LateModule.LatePing")!;

        var services = new ServiceCollection();
        services.AddMediator(builder => builder.AddHandlers(host).AddMiddleware(openMiddleware));
        services.AddMediator(builder => builder.AddHandlers(late));

        var closedMiddleware = openMiddleware.MakeGenericType(latePing, typeof(int));
        Assert.Single(services, x =>
            x.ServiceType == typeof(IMiddleware<,>).MakeGenericType(latePing, typeof(int)) &&
            x.ImplementationType == closedMiddleware);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        Assert.Equal(2, await mediator.Request(Activator.CreateInstance(latePing)!));
        Assert.Equal(1, closedMiddleware.GetField("Hits")!.GetValue(null));
    }

    // ---- the closer's methods ---------------------------------------------------------------------------------------

    [Fact]
    // The JIT compiles the closer at startup, and an application closes its middleware over every message it can name,
    // which can be thousands across its references. With every pair in one method, startup compiled all of them, for
    // middleware it never registered too. The closer now only picks the arm of the middleware it closes, and the arm
    // calls methods of at most 128 pairs, one call per pair. The generator orders messages by name, which the
    // zero-padded names keep in numeric order, so the first and last pair of each method are known.
    public async Task ManyMessages_AreClosedByMethodsOfAtMost128Pairs_CalledFromTheirMiddlewaresArm()
    {
        var source = new StringBuilder("""
                                       using System.Threading;
                                       using System.Threading.Tasks;
                                       using Sharp.Mediator;
                                       namespace ManyMessages;

                                       public sealed class Trace<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                                       {
                                           public static int Hits;

                                           public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken)
                                           {
                                               Hits++;
                                               return next(message, cancellationToken);
                                           }
                                       }

                                       public sealed class Unused<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
                                       {
                                           public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                                               next(message, cancellationToken);
                                       }

                                       """);

        for (var i = 0; i < 300; i++)
            source.AppendLine($$"""
                                public sealed record Ping{{i:D3}} : IRequest<int>;

                                public sealed class Ping{{i:D3}}Handler : IRequestHandler<Ping{{i:D3}}, int>
                                {
                                    public ValueTask<int> Handle(Ping{{i:D3}} message, CancellationToken cancellationToken) => ValueTask.FromResult({{i}});
                                }
                                """);

        var run = RunGenerator(source.ToString());
        AssertCompiles(run);

        var methods = RegistrationsText(run).Split("private static void CloseMiddleware")[1..];
        Assert.Equal(0, CountOccurrences(methods[0], "TryAddClosedMiddleware"));

        Assert.Equal(2, CountOccurrences(methods[0], "CloseMiddleware0_2(registration, middlewareType);") +
            CountOccurrences(methods[0], "CloseMiddleware1_2(registration, middlewareType);"));

        Assert.Equal([128, 128, 44, 128, 128, 44], methods[1..].Select(static x => CountOccurrences(x, "TryAddClosedMiddleware")));

        foreach (var (method, index) in methods[1..].Select(static (x, i) => (x, i)))
        {
            var pings = Regex.Matches(method, @"IMiddleware<global::ManyMessages\.Ping(\d{3}), int>")
                .Select(static x => int.Parse(x.Groups[1].Value)).ToArray();

            Assert.Equal((index % 3 * 128, Math.Min((index % 3 * 128) + 127, 299)), (pings[0], pings[^1]));
        }

        var assembly = CompileGeneratedToAssembly("ManyMessages", source.ToString());
        var trace = assembly.GetType("ManyMessages.Trace`2")!;
        var services = new ServiceCollection();
        services.AddMediator(builder => builder.AddHandlers(assembly).AddMiddleware(trace));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // The first and last pair of each method.
        foreach (var i in (int[])[0, 127, 128, 255, 256, 299])
        {
            var ping = assembly.GetType($"ManyMessages.Ping{i:D3}")!;
            Assert.Equal(i, await mediator.Request(Activator.CreateInstance(ping)!));
            Assert.Equal(1, trace.MakeGenericType(ping, typeof(int)).GetField("Hits")!.GetValue(null));
        }

        Assert.Equal(300, services.Count(x => x.ImplementationType is { IsGenericType: true } type && type.GetGenericTypeDefinition() == trace));
    }
}