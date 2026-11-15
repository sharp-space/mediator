using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sharp.Mediator.SourceGenerator.Generation;

namespace Sharp.Mediator.SourceGenerator.Analysis;

/// <summary>
/// Whether a middleware applies to a message where matching names cannot tell. The emitter matches each constraint type by name against the types a
/// message or response converts to, which misses the conversions variance adds: a <c>List&lt;string&gt;</c> response meets
/// <c>IEnumerable&lt;object&gt;</c> only through <c>IEnumerable&lt;out T&gt;</c>. For a middleware whose constraints name a variant type, the pairs that
/// name matching rejects are written into a probe as the closed types the generated code would name, and the compiler checks their constraints, as it
/// would the generated code's. Only the pairs it accepts come out, as names, so source emission stays cached while they hold.
/// </summary>
internal static class Applicability
{
    public static EquatableArray<string> VarianceSatisfied(
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<MiddlewareInfo> middlewares,
        Compilation compilation,
        CancellationToken cancellationToken
    )
    {
        var candidates = (
                from middleware in middlewares
                where middleware is { HasVariantConstraint: true, Issues: MiddlewareIssue.None }
                from handler in handlers
                where Closes(middleware, handler) &&
                    !Emitter.SatisfiesConstraints(middleware, handler.MessageFacts, handler.ResponseFacts)
                select (
                    Key: Key(middleware.MatchTypeFullyQualifiedName, handler.MessageFullyQualifiedName, handler.ResponseFullyQualifiedName),
                    Type: middleware.Arity == 1
                        ? $"{middleware.OpenBaseFullyQualifiedName}<{handler.MessageFullyQualifiedName}>"
                        : $"{middleware.OpenBaseFullyQualifiedName}<{handler.MessageFullyQualifiedName}, {handler.ResponseFullyQualifiedName}>"))
            .Distinct()
            .ToList();

        if (candidates.Count == 0) return new EquatableArray<string>([]);

        // The probe opens as a generated file does and carries the generated types' attributes, so the compiler judges
        // each pair in the context it compiles the generated code in. Judged anywhere else, a nullable warning that the
        // project treats as an error, a type marked [Obsolete(..., error: true)], or an [Experimental] type whose
        // diagnostic a #pragma turns off where it is used rejected a pair the generated code compiles. Only a
        // diagnostic that is an error by default rejects one, so no project setting turns a warning into a rejection.
        var probe = new StringBuilder();
        Emitter.AppendFileHeader(probe);
        probe.Append("namespace SharpMediatorConstraintProbe\n{\n");
        Emitter.AppendGeneratedCodeAttributes(probe, "    ");
        probe.Append("    internal static class Probe\n    {\n");

        // One field per candidate, each on its own line, so a constraint error names its candidate by line.
        var firstFieldLine = probe.ToString().Count(static x => x == '\n');
        for (var i = 0; i < candidates.Count; i++)
            probe.Append("        internal static ").Append(candidates[i].Type).Append(" Field").Append(i).Append(";\n");

        probe.Append("    }\n}\n");

        var tree = CSharpSyntaxTree.ParseText(
            probe.ToString(), (CSharpParseOptions)compilation.SyntaxTrees.First().Options, cancellationToken: cancellationToken);

        var rejected = new HashSet<int>(compilation.AddSyntaxTrees(tree).GetSemanticModel(tree)
            .GetDiagnostics(cancellationToken: cancellationToken)
            .Where(static x => x.DefaultSeverity == DiagnosticSeverity.Error)
            .Select(x => x.Location.GetLineSpan().StartLinePosition.Line - firstFieldLine));

        return new EquatableArray<string>([
            .. candidates.Where((_, index) => !rejected.Contains(index)).Select(static x => x.Key)
                .OrderBy(static x => x, StringComparer.Ordinal)
        ]);
    }

    /// <summary>The name of a pair <see cref="VarianceSatisfied" /> returns.</summary>
    public static string Key(string middleware, string message, string? response) => $"{middleware}|{message}|{response}";

    // The messages the emitter closes this middleware over, by shape.
    private static bool Closes(MiddlewareInfo middleware, HandlerInfo handler) =>
        middleware.IsStream ? handler.Kind == MessageKind.Stream :
        middleware.Arity == 1 ? handler.Kind is MessageKind.VoidRequest or MessageKind.Event :
        handler.Kind == MessageKind.Request;
}