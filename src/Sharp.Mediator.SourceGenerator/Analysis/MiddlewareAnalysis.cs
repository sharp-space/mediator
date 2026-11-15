using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sharp.Mediator.SourceGenerator.Generation;
using static Sharp.Mediator.SourceGenerator.DiagnosticDescriptors;
using static Sharp.Mediator.SourceGenerator.Analysis.HandlerAnalysis;
using static Sharp.Mediator.SourceGenerator.Analysis.SymbolNames;

namespace Sharp.Mediator.SourceGenerator.Analysis;

internal static class MiddlewareAnalysis
{
    /// <summary>Dedupes middleware infos, reports closure issues, and returns the statically closeable ones.</summary>
    internal static List<MiddlewareInfo> PrepareMiddlewares(
        DiagnosticSink report,
        ImmutableArray<MiddlewareInfo> middlewares
    )
    {
        var distinct = middlewares.Distinct()
            .OrderBy(static x => x.MatchTypeFullyQualifiedName, StringComparer.Ordinal)
            .ThenBy(static x => x.IsStream)
            .ThenBy(static x => x.Arity).ToList();

        foreach (var group in distinct.GroupBy(static x => x.MatchTypeFullyQualifiedName))
        {
            var unsupported = group.Any(static x =>
                (x.Issues & MiddlewareIssue.UnsupportedOpenGenericShape) != 0);

            if (unsupported)
                report(UnsupportedOpenGenericMiddleware, group.First().Location, SimpleName(group.Key));
            else if (group.Any(static x => (x.Issues & MiddlewareIssue.Inaccessible) != 0))
                report(MiddlewareNotStaticallyCloseable, group.First().Location, SimpleName(group.Key));
        }

        return distinct.Where(static x => x.Issues == MiddlewareIssue.None).ToList();
    }

    internal static EquatableArray<MiddlewareInfo>? GetMiddlewares(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var declaration = (TypeDeclarationSyntax)node;
        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken)!;
        if (symbol.TypeKind != TypeKind.Class || symbol.IsAbstract) return null;

        var middlewares = Discovery.GetMiddlewares(
            symbol, semanticModel.Compilation.Assembly).ToImmutableArray();

        return middlewares.Length > 0 ? new EquatableArray<MiddlewareInfo>(middlewares) : null;
    }

    internal static EquatableArray<UnreachableMiddlewareInfo>? GetUnreachableMiddlewares(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var declaration = (TypeDeclarationSyntax)node;
        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken)!;
        if (symbol.TypeKind != TypeKind.Class || symbol.IsAbstract || symbol.IsGenericType)
            return null;

        var middlewares = Discovery.GetUnreachableMiddlewares(symbol).ToImmutableArray();
        return middlewares.Length > 0 ? new EquatableArray<UnreachableMiddlewareInfo>(middlewares) : null;
    }
}