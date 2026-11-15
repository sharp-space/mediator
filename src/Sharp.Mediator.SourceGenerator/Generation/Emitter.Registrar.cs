using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Sharp.Mediator.SourceGenerator.Analysis;

namespace Sharp.Mediator.SourceGenerator.Generation;

internal static partial class Emitter
{
    // The generated-code contract the emitted registrar is written for, which it passes to MediatorRuntime.AddAssembly:
    // MediatorRuntime.Contract in the runtime, which this must equal.
    private const int Contract = 1;

    // Runs once per runtime type at most, and again after another assembly adds its finders: dispatch memoizes what
    // this returns in a routing that the next assembly replaces. The closed factory calls also root the routes
    // under Native AOT.
    //
    // The JIT compiles a finder at the first dispatch that misses, and loads every type and closes every factory method
    // its routes name: with 2000 routes that made a first dispatch take 20 ms rather than 3. So a finder of more routes
    // than RoutesPerFinderMethod sorts them into methods by a hash of the message type's name, and calls only the
    // method of the type it is asked for. Where dynamic code is not supported, as under Native AOT, nothing compiles
    // when the program runs, so the finder calls every method in turn and reads no type names. Returns whether the
    // finder hashes names, which needs HashName.
    private const int RoutesPerFinderMethod = 16;

    // CloseMiddleware closes each requested open-generic middleware over this compilation's messages, and over the
    // referenced messages no generator that saw both closes, marking every evaluated pair as "considered" — including
    // constraint-excluded ones, so the runtime's cross-assembly gap pass (which closes whatever no compilation
    // considered) never mistakes a deliberate exclusion for a gap.
    //
    // The JIT compiles the closer at startup, and an application that references many messages closes each of its
    // middleware over all of them. One method of every pair took 33 ms to compile and run where 1000 referenced
    // messages met two middleware, only one of them registered. So CloseMiddleware only picks the definition's arm, and
    // the arm calls methods of at most ClosingsPerMethod pairs, one call per pair: about 12 ms for the same messages,
    // against about 8 ms for closing them with MakeGenericType, which Native AOT does not allow.
    private const int ClosingsPerMethod = 128;

    private static void EmitRegistrar(
        StringBuilder stringBuilder,
        List<HandlerInfo> voidRequests,
        List<HandlerInfo> responseRequests,
        List<HandlerInfo> events,
        List<HandlerInfo> streams,
        List<ClosingTarget> referencedMessages,
        List<MiddlewareInfo> middlewares,
        HashSet<string> varianceSatisfied
    )
    {
        AppendGeneratedCodeAttributes(stringBuilder, "    ");
        stringBuilder.AppendLine("    internal static class GeneratedMediatorRegistrar");
        stringBuilder.AppendLine("    {");

        var interfaceEvents = events.Where(static x => x.MessageIsInterface).ToList();
        stringBuilder.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        stringBuilder.AppendLine("        internal static void Initialize() =>");
        stringBuilder.AppendLine(
            $"            global::Sharp.Mediator.Bindings.MediatorRuntime.AddAssembly({Contract}, typeof(GeneratedMediatorRegistrar).Assembly, FindRequestRoute, FindEventRoute, {(interfaceEvents.Count > 0 ? "MatchEventInterfaces" : "null")}, FindStreamRoute, HandlerServices, {(middlewares.Count > 0 ? "CloseMiddleware" : "null")});");

        stringBuilder.AppendLine();

        const string factory = "global::Sharp.Mediator.Bindings.MediatorFactory";
        var hashesNames = EmitRouteFinder(
            stringBuilder, "FindRequestRoute",
            responseRequests.Select(static x => (x.MessageMetadataName, x.MessageFullyQualifiedName,
                    $"{factory}.CreateRequestRoute<{x.MessageFullyQualifiedName}, {x.ResponseFullyQualifiedName}>()"))
                .Concat(voidRequests.Select(static x => (x.MessageMetadataName, x.MessageFullyQualifiedName,
                    $"{factory}.CreateVoidRequestRoute<{x.MessageFullyQualifiedName}>()"))));

        hashesNames |= EmitRouteFinder(
            stringBuilder, "FindEventRoute",
            events.Select(static x =>
                (x.MessageMetadataName, x.MessageFullyQualifiedName, $"{factory}.CreateEventRoute<{x.MessageFullyQualifiedName}>()")));

        hashesNames |= EmitRouteFinder(
            stringBuilder, "FindStreamRoute",
            streams.Select(static x => (x.MessageMetadataName, x.MessageFullyQualifiedName,
                $"{factory}.CreateStreamRoute<{x.MessageFullyQualifiedName}, {x.ResponseFullyQualifiedName}>()")));

        if (hashesNames) EmitHashName(stringBuilder);
        if (interfaceEvents.Count > 0) EmitEventInterfaceMatcher(stringBuilder, interfaceEvents);

        EmitHandlerServices(stringBuilder, voidRequests, responseRequests, events, streams);
        if (middlewares.Count > 0)
        {
            EmitMiddlewareCloser(
                stringBuilder,
                Targets(voidRequests, referencedMessages, MessageKind.VoidRequest),
                Targets(responseRequests, referencedMessages, MessageKind.Request),
                Targets(events, referencedMessages, MessageKind.Event),
                Targets(streams, referencedMessages, MessageKind.Stream),
                middlewares, varianceSatisfied);
        }

        stringBuilder.AppendLine("    }");
    }

    private static bool EmitRouteFinder(
        StringBuilder stringBuilder,
        string name,
        IEnumerable<(string Name, string Message, string Create)> routes
    )
    {
        var all = routes.ToList();
        if (all.Count <= RoutesPerFinderMethod)
        {
            EmitRouteChecks(stringBuilder, name, all);
            return false;
        }

        var buckets = (uint)((all.Count + RoutesPerFinderMethod - 1) / RoutesPerFinderMethod);
        var methods = all.GroupBy(x => HashName(x.Name) % buckets).OrderBy(static x => x.Key).ToList();
        stringBuilder.AppendLine($"        private static object? {name}(global::System.Type messageType)");
        stringBuilder.AppendLine("        {");
        stringBuilder.AppendLine(
            $"            if (!global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return {name}InTurn(messageType);");

        stringBuilder.AppendLine($"            switch (HashName(messageType.Name) % {buckets}u)");
        stringBuilder.AppendLine("            {");
        foreach (var method in methods)
            stringBuilder.AppendLine($"                case {method.Key}u: return {name}{method.Key}(messageType);");

        stringBuilder.AppendLine("                default: return null;");
        stringBuilder.AppendLine("            }");
        stringBuilder.AppendLine("        }");
        stringBuilder.AppendLine();

        // One statement per method: a chain of ?? as long as an application's buckets would nest too deep to compile.
        stringBuilder.AppendLine($"        private static object? {name}InTurn(global::System.Type messageType)");
        stringBuilder.AppendLine("        {");
        stringBuilder.AppendLine("            object? route;");
        foreach (var method in methods)
            stringBuilder.AppendLine($"            if ((route = {name}{method.Key}(messageType)) is not null) return route;");

        stringBuilder.AppendLine("            return null;");
        stringBuilder.AppendLine("        }");
        stringBuilder.AppendLine();
        foreach (var method in methods) EmitRouteChecks(stringBuilder, $"{name}{method.Key}", method);

        return true;
    }

    private static void EmitRouteChecks(
        StringBuilder stringBuilder,
        string name,
        IEnumerable<(string Name, string Message, string Create)> routes
    )
    {
        stringBuilder.AppendLine($"        private static object? {name}(global::System.Type messageType)");
        stringBuilder.AppendLine("        {");
        foreach (var (_, message, create) in routes)
            stringBuilder.AppendLine($"            if (messageType == typeof({message})) return {create};");

        stringBuilder.AppendLine("            return null;");
        stringBuilder.AppendLine("        }");
        stringBuilder.AppendLine();
    }

    // FNV-1a over the UTF-16 code units of a type's metadata name: a nested type's own name, and a generic type's name
    // with its arity, as in Wrapper`1. The generated HashName repeats it over Type.Name, which returns that name
    // wherever dynamic code is supported.
    private static uint HashName(string name)
    {
        var hash = 2166136261u;
        foreach (var character in name) hash = unchecked((hash ^ character) * 16777619u);

        return hash;
    }

    private static void EmitHashName(StringBuilder stringBuilder)
    {
        stringBuilder.AppendLine("        private static uint HashName(string name)");
        stringBuilder.AppendLine("        {");
        stringBuilder.AppendLine("            var hash = 2166136261u;");
        stringBuilder.AppendLine("            foreach (var character in name) hash = unchecked((hash ^ character) * 16777619u);");
        stringBuilder.AppendLine("            return hash;");
        stringBuilder.AppendLine("        }");
        stringBuilder.AppendLine();
    }

    // Adds each interface with event handlers here that the event is. An `is` check keeps what it checks
    // through trimming, where the runtime listing a type's interfaces by reflection would not. Runs as often as
    // the route finders do.
    private static void EmitEventInterfaceMatcher(StringBuilder stringBuilder, List<HandlerInfo> interfaceEvents)
    {
        stringBuilder.AppendLine(
            "        private static void MatchEventInterfaces(object message, global::System.Collections.Generic.ISet<global::System.Type> types)");

        stringBuilder.AppendLine("        {");
        foreach (var @event in interfaceEvents)
            stringBuilder.AppendLine(
                $"            if (message is {@event.MessageFullyQualifiedName}) types.Add(typeof({@event.MessageFullyQualifiedName}));");

        stringBuilder.AppendLine("        }");
        stringBuilder.AppendLine();
    }

    // Declares what this compilation can dispatch. A method rather than registrar statements, so that the types stay
    // unloaded on a startup that never asks.
    private static void EmitHandlerServices(
        StringBuilder stringBuilder,
        List<HandlerInfo> voidRequests,
        List<HandlerInfo> responseRequests,
        List<HandlerInfo> events,
        List<HandlerInfo> streams
    )
    {
        stringBuilder.AppendLine("        private static global::System.Type[] HandlerServices() => new global::System.Type[]");
        stringBuilder.AppendLine("        {");
        foreach (var request in voidRequests)
            stringBuilder.AppendLine($"            typeof(global::Sharp.Mediator.IRequestHandler<{request.MessageFullyQualifiedName}>),");

        foreach (var request in responseRequests)
            stringBuilder.AppendLine(
                $"            typeof(global::Sharp.Mediator.IRequestHandler<{request.MessageFullyQualifiedName}, {request.ResponseFullyQualifiedName}>),");

        foreach (var @event in events)
            stringBuilder.AppendLine($"            typeof(global::Sharp.Mediator.IEventHandler<{@event.MessageFullyQualifiedName}>),");

        foreach (var stream in streams)
            stringBuilder.AppendLine(
                $"            typeof(global::Sharp.Mediator.IStreamRequestHandler<{stream.MessageFullyQualifiedName}, {stream.ResponseFullyQualifiedName}>),");

        stringBuilder.AppendLine("        };");
    }

    // This compilation's messages first, then the referenced ones of the same kind.
    private static List<ClosingTarget> Targets(List<HandlerInfo> local, List<ClosingTarget> referenced, MessageKind kind) =>
        [.. local.Select(static x => new ClosingTarget(x, default)), .. referenced.Where(x => x.Handler.Kind == kind)];

    private static void EmitMiddlewareCloser(
        StringBuilder stringBuilder,
        List<ClosingTarget> voidRequests,
        List<ClosingTarget> responseRequests,
        List<ClosingTarget> events,
        List<ClosingTarget> streams,
        List<MiddlewareInfo> middlewares,
        HashSet<string> varianceSatisfied
    )
    {
        // One arm per open-generic definition; a definition implementing several middleware interfaces gets all of its
        // registrations inside the same arm.
        var arms = middlewares
            .GroupBy(static x => x.MatchTypeFullyQualifiedName)
            .Select(group => (Definition: group.Key, Methods: Split([
                .. group.SelectMany(middleware => ArmClosings(voidRequests, responseRequests, events, streams, middleware, varianceSatisfied))
            ]))).ToList();

        stringBuilder.AppendLine();
        stringBuilder.AppendLine(
            "        private static void CloseMiddleware(global::Sharp.Mediator.Bindings.MediatorRegistration registration, global::System.Type middlewareType)");

        stringBuilder.AppendLine("        {");
        for (var arm = 0; arm < arms.Count; arm++)
        {
            stringBuilder.AppendLine($"            if (middlewareType == typeof({arms[arm].Definition}))");
            stringBuilder.AppendLine("            {");
            for (var method = 0; method < arms[arm].Methods.Count; method++)
                stringBuilder.AppendLine($"                CloseMiddleware{arm}_{method}(registration, middlewareType);");

            stringBuilder.AppendLine("                return;");
            stringBuilder.AppendLine("            }");
        }

        stringBuilder.AppendLine("        }");
        for (var arm = 0; arm < arms.Count; arm++)
        for (var method = 0; method < arms[arm].Methods.Count; method++)
        {
            stringBuilder.AppendLine();
            stringBuilder.AppendLine(
                $"        private static void CloseMiddleware{arm}_{method}(global::Sharp.Mediator.Bindings.MediatorRegistration registration, global::System.Type middlewareType)");

            stringBuilder.AppendLine("        {");
            foreach (var closing in arms[arm].Methods[method])
                stringBuilder.AppendLine($"            {closing}");

            stringBuilder.AppendLine("        }");
        }
    }

    // The closings of one arm in runs of at most ClosingsPerMethod, the body of one method each.
    private static List<List<string>> Split(List<string> closings)
    {
        var methods = new List<List<string>>();
        for (var start = 0; start < closings.Count; start += ClosingsPerMethod)
            methods.Add(closings.GetRange(start, Math.Min(ClosingsPerMethod, closings.Count - start)));

        return methods;
    }

    private static IEnumerable<string> ArmClosings(
        List<ClosingTarget> voidRequests,
        List<ClosingTarget> responseRequests,
        List<ClosingTarget> events,
        List<ClosingTarget> streams,
        MiddlewareInfo middleware,
        HashSet<string> varianceSatisfied
    )
    {
        if (middleware.IsStream)
        {
            return Open(streams, middleware).Select(stream => Closing(
                $"global::Sharp.Mediator.IStreamMiddleware<{stream.MessageFullyQualifiedName}, {stream.ResponseFullyQualifiedName}>",
                $"{middleware.OpenBaseFullyQualifiedName}<{stream.MessageFullyQualifiedName}, {stream.ResponseFullyQualifiedName}>",
                Applies(middleware, stream, varianceSatisfied)));
        }

        if (middleware.Arity == 1)
        {
            return Open(voidRequests, middleware).Concat(Open(events, middleware)).Select(message => Closing(
                $"global::Sharp.Mediator.IMiddleware<{message.MessageFullyQualifiedName}>",
                $"{middleware.OpenBaseFullyQualifiedName}<{message.MessageFullyQualifiedName}>",
                Applies(middleware, message, varianceSatisfied)));
        }

        return Open(responseRequests, middleware).Select(request => Closing(
            $"global::Sharp.Mediator.IMiddleware<{request.MessageFullyQualifiedName}, {request.ResponseFullyQualifiedName}>",
            $"{middleware.OpenBaseFullyQualifiedName}<{request.MessageFullyQualifiedName}, {request.ResponseFullyQualifiedName}>",
            Applies(middleware, request, varianceSatisfied)));
    }

    // The messages of these targets that no other generator closes this middleware over.
    private static IEnumerable<HandlerInfo> Open(List<ClosingTarget> targets, MiddlewareInfo middleware) =>
        targets.Where(x => !x.ClosedElsewhere.Contains(middleware.MatchTypeFullyQualifiedName)).Select(static x => x.Handler);

    // Matched by name, or else checked by the compiler for a middleware that names a variant type (see Applicability).
    private static bool Applies(MiddlewareInfo middleware, HandlerInfo handler, HashSet<string> varianceSatisfied) =>
        SatisfiesConstraints(middleware, handler.MessageFacts, handler.ResponseFacts) ||
        varianceSatisfied.Contains(Applicability.Key(
            middleware.MatchTypeFullyQualifiedName, handler.MessageFullyQualifiedName, handler.ResponseFullyQualifiedName));

    // A pair the constraints exclude has no implementation type to name, so it is only marked considered.
    private static string Closing(string serviceType, string implementationType, bool satisfiesConstraints) =>
        satisfiesConstraints
            ? $"registration.TryAddClosedMiddleware<{serviceType}, {implementationType}>(middlewareType);"
            : $"registration.MarkMiddlewareConsidered(middlewareType, typeof({serviceType}));";

    // Assembly-keyed handler registrations drained by builder.AddHandlers(Assembly), the trim/AOT-safe replacement for
    // the reflection scan. Mirrors Sharp.Validator's GeneratedValidatorRegistrar.
    private static void EmitHandlerRegistrar(StringBuilder stringBuilder, ImmutableArray<HandlerInfo> handlers)
    {
        var discoverable = handlers
            .Where(static x => x is { HandlerAccessible: true, MessageAccessible: true })
            .Select(static x => (x.ServiceInterfaceFullyQualifiedName, x.HandlerFullyQualifiedName))
            .Distinct()
            .OrderBy(static x => x.ServiceInterfaceFullyQualifiedName + x.HandlerFullyQualifiedName, StringComparer.Ordinal)
            .ToList();

        AppendGeneratedCodeAttributes(stringBuilder, "    ");
        stringBuilder.AppendLine("    internal static class GeneratedHandlerRegistrar");
        stringBuilder.AppendLine("    {");
        stringBuilder.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        stringBuilder.AppendLine("        internal static void Initialize() =>");
        stringBuilder.AppendLine(
            "            global::Sharp.Mediator.Bindings.HandlerRegistry.AddRegistrar(typeof(GeneratedHandlerRegistrar).Assembly, RegisterHandlers);");

        stringBuilder.AppendLine();
        stringBuilder.AppendLine("        private static void RegisterHandlers(global::Sharp.Mediator.Bindings.MediatorRegistration registration)");
        stringBuilder.AppendLine("        {");
        foreach (var (service, handler) in discoverable)
            stringBuilder.AppendLine($"            registration.TryAddEnumerable(typeof({service}), typeof({handler}));");

        stringBuilder.AppendLine("        }");
        stringBuilder.AppendLine("    }");
    }
}