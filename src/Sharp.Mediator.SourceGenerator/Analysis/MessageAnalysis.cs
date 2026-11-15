using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Sharp.Mediator.SourceGenerator.Generation;
using static Sharp.Mediator.SourceGenerator.DiagnosticDescriptors;
using static Sharp.Mediator.SourceGenerator.Analysis.HandlerAnalysis;
using static Sharp.Mediator.SourceGenerator.Analysis.SymbolNames;

namespace Sharp.Mediator.SourceGenerator.Analysis;

internal static class MessageAnalysis
{
    /// <summary>Reports SMD001/SMD002/SMD004 and returns the set of message types excluded from emission.</summary>
    internal static HashSet<string> ReportInvalidMessageShapes(
        DiagnosticSink report,
        ImmutableArray<HandlerInfo> handlers
    )
    {
        var invalid = new HashSet<string>(StringComparer.Ordinal);

        ReportMultipleResponses(
            report, handlers, MessageKind.Request, MultipleRequestResponses, invalid);

        ReportMultipleResponses(
            report, handlers, MessageKind.Stream, MultipleStreamResponses, invalid);

        foreach (var group in handlers
                     .Where(static x =>
                         x.Kind is MessageKind.VoidRequest or MessageKind.Request &&
                         x.MessageImplementsBothRequestInterfaces)
                     .GroupBy(static x => x.MessageFullyQualifiedName)
                     .OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            invalid.Add(group.Key);
            report(BothRequestInterfaces, group.First().HandlerLocation, SimpleName(group.Key));
        }

        return invalid;
    }

    /// <summary>Reports SMD008 and excludes the interface from emission: no request or stream is one at runtime.</summary>
    internal static void ReportInterfaceRequestHandlers(
        DiagnosticSink report,
        ImmutableArray<HandlerInfo> handlers,
        HashSet<string> invalid
    )
    {
        foreach (var handler in handlers
                     .Where(static x => x is { MessageIsInterface: true, Kind: not MessageKind.Event })
                     .Select(static x => (x.HandlerFullyQualifiedName, x.MessageFullyQualifiedName, x.HandlerLocation))
                     .Distinct()
                     .OrderBy(static x => x.HandlerFullyQualifiedName + x.MessageFullyQualifiedName, StringComparer.Ordinal))
        {
            invalid.Add(handler.MessageFullyQualifiedName);
            report(
                InterfaceRequestHandler, handler.HandlerLocation,
                SimpleName(handler.HandlerFullyQualifiedName), SimpleName(handler.MessageFullyQualifiedName));
        }
    }

    /// <summary>One message type must map to exactly one response type — per kind, across every discovered handler.</summary>
    private static void ReportMultipleResponses(
        DiagnosticSink report,
        ImmutableArray<HandlerInfo> handlers,
        MessageKind kind,
        DiagnosticDescriptor descriptor,
        HashSet<string> invalid
    )
    {
        foreach (var group in handlers
                     .GroupBy(static x => (x.Kind, x.MessageFullyQualifiedName))
                     .OrderBy(static x => x.Key.MessageFullyQualifiedName, StringComparer.Ordinal))
        {
            if (group.Key.Kind != kind) continue;

            // Distinct response handler interfaces imply the message itself implements those same distinct
            // IRequest<T>/IStreamRequest<T> interfaces, which Discovery records on every HandlerInfo in the group.
            var multipleResponses = group.First().MessageImplementsMultipleResponseInterfaces;
            if (!multipleResponses) continue;

            invalid.Add(group.Key.MessageFullyQualifiedName);
            report(descriptor, group.First().HandlerLocation, SimpleName(group.Key.MessageFullyQualifiedName));
        }
    }

    /// <summary>Reports SMD003 and returns the set of message types excluded because generated code cannot reference them.</summary>
    internal static HashSet<string> ReportInaccessibleMessages(
        DiagnosticSink report,
        ImmutableArray<HandlerInfo> handlers
    )
    {
        var inaccessible = new HashSet<string>(StringComparer.Ordinal);

        // A partial handler yields one HandlerInfo per declaration, all with the same location.
        foreach (var handler in handlers
                     .Where(static x => !x.MessageAccessible)
                     .Select(static x => (x.MessageFullyQualifiedName, x.HandlerFullyQualifiedName, x.HandlerLocation))
                     .Distinct()
                     .OrderBy(static x => x.MessageFullyQualifiedName + x.HandlerFullyQualifiedName, StringComparer.Ordinal))
        {
            inaccessible.Add(handler.MessageFullyQualifiedName);
            report(
                InaccessibleMessage, handler.HandlerLocation,
                SimpleName(handler.MessageFullyQualifiedName), SimpleName(handler.HandlerFullyQualifiedName));
        }

        return inaccessible;
    }
}