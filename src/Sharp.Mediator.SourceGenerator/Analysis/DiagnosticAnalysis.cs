using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sharp.Mediator.SourceGenerator.Generation;

namespace Sharp.Mediator.SourceGenerator.Analysis;

internal static class DiagnosticAnalysis
{
    internal static void Analyze(Compilation compilation, CancellationToken cancellationToken, Action<Diagnostic> report)
    {
        var handlers = ImmutableArray.CreateBuilder<HandlerInfo>();
        var middlewares = ImmutableArray.CreateBuilder<MiddlewareInfo>();
        var unreachable = ImmutableArray.CreateBuilder<UnreachableMiddlewareInfo>();
        var generic = ImmutableArray.CreateBuilder<GenericHandlerInfo>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable RS1030 // Cross-declaration diagnostics require the compilation's current semantic models.
            var model = compilation.GetSemanticModel(tree);
#pragma warning restore RS1030

            foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (declaration is not (ClassDeclarationSyntax or RecordDeclarationSyntax) || declaration.BaseList is null)
                    continue;

                if (HandlerAnalysis.GetHandlers(declaration, model, cancellationToken) is { } foundHandlers)
                    handlers.AddRange(foundHandlers);

                if (declaration.TypeParameterList is not null &&
                    MiddlewareAnalysis.GetMiddlewares(declaration, model, cancellationToken) is { } foundMiddlewares)
                    middlewares.AddRange(foundMiddlewares);

                if (MiddlewareAnalysis.GetUnreachableMiddlewares(declaration, model, cancellationToken) is { } foundUnreachable)
                    unreachable.AddRange(foundUnreachable);

                if (HandlerAnalysis.GetGenericHandler(declaration, model, cancellationToken) is { } foundGeneric)
                    generic.Add(foundGeneric);
            }
        }

        middlewares.AddRange(ReferenceAnalysis.ScanReferencedAssemblies(compilation, cancellationToken).Middlewares);
        HandlerAnalysis.ReportDiagnostics(report, handlers.ToImmutable(), middlewares.ToImmutable(),
            unreachable.ToImmutable(), generic.ToImmutable(), compilation);
    }
}