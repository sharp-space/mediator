using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Sharp.Mediator.SourceGenerator.Generation;

/// <summary>Symbol-level inspection that mirrors, at compile time, the reflection the library used to do at runtime.</summary>
internal static class Discovery
{
    private const string MediatorNamespace = "Sharp.Mediator";

    private static readonly SymbolDisplayFormat FullyQualifiedNameFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    // Constraint satisfaction is decided on these strings, and a tuple's element names are not part of its identity:
    // `IRequest<(int A, string B)>` and `IRequestHandler<M, (int X, string Y)>` agree on the response type.
    private static readonly SymbolDisplayFormat ConstraintNameFormat = FullyQualifiedNameFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.ExpandValueTuple);

    /// <summary>True when <paramref name="symbol" />'s original definition is the named Sharp.Mediator type (e.g. <c>IRequestHandler`2</c>).</summary>
    internal static bool IsMediatorType(ITypeSymbol symbol, string metadataName) =>
        symbol.MetadataName == metadataName &&
        symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace &&
        containingNamespace.ToDisplayString() == MediatorNamespace;

    /// <summary>Enumerates the handler interfaces a concrete type implements.</summary>
    internal static IEnumerable<HandlerInfo> GetHandlers(INamedTypeSymbol type, IAssemblySymbol generatedAssembly)
    {
        var handlerFullyQualifiedName = type.ToDisplayString(FullyQualifiedNameFormat);
        var handlerAccessible = IsAccessibleFromGeneratedCode(type, generatedAssembly);
        var handlerLocation = LocationSnapshot.From(type.Locations.FirstOrDefault(static x => x.IsInSource) ?? Location.None);
        foreach (var interfaceType in type.AllInterfaces)
        {
            var definition = interfaceType.OriginalDefinition;
            if (IsMediatorType(definition, "IRequestHandler`1"))
            {
                var message = interfaceType.TypeArguments[0];
                yield return BuildHandlerInfo(
                    handlerFullyQualifiedName, MessageKind.VoidRequest, message, null,
                    interfaceType, handlerAccessible, generatedAssembly, handlerLocation);
            }
            else if (IsMediatorType(definition, "IRequestHandler`2"))
            {
                var message = interfaceType.TypeArguments[0];
                var response = interfaceType.TypeArguments[1];
                yield return BuildHandlerInfo(
                    handlerFullyQualifiedName, MessageKind.Request, message, response,
                    interfaceType, handlerAccessible, generatedAssembly, handlerLocation);
            }
            else if (IsMediatorType(definition, "IEventHandler`1"))
            {
                var message = interfaceType.TypeArguments[0];
                yield return BuildHandlerInfo(
                    handlerFullyQualifiedName, MessageKind.Event, message, null,
                    interfaceType, handlerAccessible, generatedAssembly, handlerLocation);
            }
            else if (IsMediatorType(definition, "IStreamRequestHandler`2"))
            {
                var message = interfaceType.TypeArguments[0];
                var response = interfaceType.TypeArguments[1];
                yield return BuildHandlerInfo(
                    handlerFullyQualifiedName, MessageKind.Stream, message, response,
                    interfaceType, handlerAccessible, generatedAssembly, handlerLocation);
            }
        }
    }

    /// <summary>True when <paramref name="type" /> implements any closed handler interface.</summary>
    internal static bool ImplementsHandlerInterface(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(static x => IsMediatorType(x.OriginalDefinition, "IRequestHandler`1") ||
            IsMediatorType(x.OriginalDefinition, "IRequestHandler`2") ||
            IsMediatorType(x.OriginalDefinition, "IEventHandler`1") ||
            IsMediatorType(x.OriginalDefinition, "IStreamRequestHandler`2"));

    private static HandlerInfo BuildHandlerInfo(
        string handlerFullyQualifiedName,
        MessageKind kind,
        ITypeSymbol message,
        ITypeSymbol? response,
        INamedTypeSymbol serviceInterface,
        bool handlerAccessible,
        IAssemblySymbol generatedAssembly,
        LocationSnapshot handlerLocation
    )
    {
        var isRequestKind = kind is MessageKind.VoidRequest or MessageKind.Request;
        var multipleResponseInterfaces = kind switch
        {
            MessageKind.Request => ImplementsMultipleResponseInterfaces(message, "IRequest`1"),
            MessageKind.Stream => ImplementsMultipleResponseInterfaces(message, "IStreamRequest`1"),
            _ => false
        };

        return new HandlerInfo(
            handlerFullyQualifiedName,
            kind,
            message.ToDisplayString(FullyQualifiedNameFormat),
            message.MetadataName,
            response?.ToDisplayString(FullyQualifiedNameFormat),
            isRequestKind && ImplementsBothRequestInterfaces(message),
            multipleResponseInterfaces,
            serviceInterface.ToDisplayString(FullyQualifiedNameFormat),
            handlerAccessible,
            TypeAccessibleFromGeneratedCode(message, generatedAssembly) &&
            (response is null || TypeAccessibleFromGeneratedCode(response, generatedAssembly)),
            message.TypeKind == TypeKind.Interface,
            BuildTypeFacts(message),
            response is null ? default : BuildTypeFacts(response),
            handlerLocation);
    }

    /// <summary>
    /// Enumerates the middleware interfaces a generic type definition implements, with the constraint metadata needed to close it over each applicable
    /// message. Closed middleware is registered at runtime from its already-closed interfaces and never flows through the generator.
    /// </summary>
    internal static IEnumerable<MiddlewareInfo> GetMiddlewares(INamedTypeSymbol definition, IAssemblySymbol generatedAssembly)
    {
        var accessible = IsAccessibleFromGeneratedCode(definition, generatedAssembly);
        var location = LocationSnapshot.From(
            definition.Locations.FirstOrDefault(static x => x.IsInSource) ?? Location.None);

        var unbound = definition.ConstructUnboundGenericType().ToDisplayString(FullyQualifiedNameFormat);
        var unsupportedContainer = definition.ContainingType?.IsGenericType == true;
        var openBase = unsupportedContainer ? "" : unbound.Substring(0, unbound.IndexOf('<'));
        foreach (var interfaceType in definition.AllInterfaces)
        {
            var interfaceDefinition = interfaceType.OriginalDefinition;
            bool isStream;
            int arity;
            if (IsMediatorType(interfaceDefinition, "IMiddleware`1"))
            {
                isStream = false;
                arity = 1;
            }
            else if (IsMediatorType(interfaceDefinition, "IMiddleware`2"))
            {
                isStream = false;
                arity = 2;
            }
            else if (IsMediatorType(interfaceDefinition, "IStreamMiddleware`2"))
            {
                isStream = true;
                arity = 2;
            }
            else continue;

            // Closing emits OpenBase<Message(, Response)>, which is only meaningful when the middleware's type
            // parameters are exactly the interface's type arguments, in order. Anything more exotic (fixed, reordered,
            // unused, or generic-containing-type parameters) is diagnosed and must be registered as the required closed
            // middleware types; the runtime builder enforces the same contract.
            var parametersMatchInterface = !unsupportedContainer &&
                definition.TypeParameters.Length == interfaceType.TypeArguments.Length &&
                definition.TypeParameters
                    .Select(static (parameter, index) => (Parameter: parameter, Index: index))
                    .All(x => SymbolEqualityComparer.Default.Equals(interfaceType.TypeArguments[x.Index], x.Parameter));

            if (!parametersMatchInterface)
            {
                yield return new MiddlewareInfo(
                    isStream, arity, unbound, openBase,
                    (accessible ? MiddlewareIssue.None : MiddlewareIssue.Inaccessible) |
                    MiddlewareIssue.UnsupportedOpenGenericShape,
                    "", default, default, false, definition.ContainingAssembly.Name, location);

                continue;
            }

            var typeParameterNames = string.Join(
                ";", definition.TypeParameters.Select(static x => x.Name));

            var messageRequirements = BuildRequirements(definition.TypeParameters[0]);
            var responseRequirements = arity == 2
                ? BuildRequirements(definition.TypeParameters[1])
                : default;

            yield return new MiddlewareInfo(
                isStream, arity, unbound, openBase,
                accessible ? MiddlewareIssue.None : MiddlewareIssue.Inaccessible,
                typeParameterNames, messageRequirements, responseRequirements, HasVariantConstraint(definition),
                definition.ContainingAssembly.Name, location);
        }
    }

    /// <summary>
    /// Enumerates the closed middleware interfaces of <paramref name="type" /> that no pipeline resolves: void requests and events use
    /// <c>IMiddleware&lt;T&gt;</c>, response requests <c>IMiddleware&lt;T, R&gt;</c>, and streams their own interface. The generic constraints only require
    /// <c>IMessage</c>, so the mismatch compiles and registers.
    /// </summary>
    internal static IEnumerable<UnreachableMiddlewareInfo> GetUnreachableMiddlewares(INamedTypeSymbol type)
    {
        foreach (var interfaceType in type.AllInterfaces)
        {
            var definition = interfaceType.OriginalDefinition;
            var isVoidShape = IsMediatorType(definition, "IMiddleware`1");
            if (!isVoidShape && !IsMediatorType(definition, "IMiddleware`2"))
                continue;

            var message = interfaceType.TypeArguments[0];

            // An event dispatches at its interface levels too, IEvent itself among them, which is not among its own
            // interfaces. A request dispatches only at classes and structs, so IRequest itself stays unreachable.
            var reachable = isVoidShape
                ? IsMediatorType(message, "IEvent") ||
                message.AllInterfaces.Any(static x => IsMediatorType(x, "IRequest") || IsMediatorType(x, "IEvent"))
                : message.AllInterfaces.Any(static x => IsMediatorType(x.OriginalDefinition, "IRequest`1"));

            if (reachable) continue;

            yield return new UnreachableMiddlewareInfo(
                type.ToDisplayString(FullyQualifiedNameFormat),
                interfaceType.ToDisplayString(FullyQualifiedNameFormat),
                message.ToDisplayString(FullyQualifiedNameFormat),
                LocationSnapshot.From(type.Locations.First(static x => x.IsInSource)));
        }
    }

    // Whether a constraint is a variant interface: a message can then meet it through a variance conversion, which the
    // names of the types the message converts to do not show. Variance inside an invariant constraint converts nothing,
    // and a constraint cannot be a delegate.
    private static bool HasVariantConstraint(INamedTypeSymbol definition) =>
        definition.TypeParameters.SelectMany(static x => x.ConstraintTypes).OfType<INamedTypeSymbol>()
            .Any(static x => x.OriginalDefinition.TypeParameters.Any(static parameter => parameter.Variance != VarianceKind.None));

    private static TypeParameterRequirements BuildRequirements(ITypeParameterSymbol typeParameter)
    {
        List<string> constraintTypes = [];
        List<string> substitutableConstraintTypes = [];
        foreach (var constraint in typeParameter.ConstraintTypes)
            // Constraints that reference the middleware's own type parameters (IMessage<TResponse>,
            // `TResponse : TMessage`, IComparable<TResponse>, …) render those parameters by simple name; the emitter
            // substitutes each name with the candidate's closed type before matching.
            if (ContainsTypeParameter(constraint))
                substitutableConstraintTypes.Add(constraint.ToDisplayString(ConstraintNameFormat));
            else
                constraintTypes.Add(constraint.ToDisplayString(ConstraintNameFormat));

        constraintTypes.Sort(StringComparer.Ordinal);
        substitutableConstraintTypes.Sort(StringComparer.Ordinal);
        return new TypeParameterRequirements(
            typeParameter.HasReferenceTypeConstraint,
            typeParameter.HasValueTypeConstraint || typeParameter.HasUnmanagedTypeConstraint,
            typeParameter.HasUnmanagedTypeConstraint,
            typeParameter.HasConstructorConstraint,
            string.Join(";", constraintTypes),
            string.Join(";", substitutableConstraintTypes));
    }

    internal static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        INamedTypeSymbol named =>
            named.TypeArguments.Any(ContainsTypeParameter) ||
            (named.ContainingType is { } containingType && ContainsTypeParameter(containingType)),
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        _ => false
    };

    private static TypeFacts BuildTypeFacts(ITypeSymbol type)
    {
        List<string> compatibleTypes = [];
        for (var current = type; current is not null; current = current.BaseType)
            compatibleTypes.Add(current.ToDisplayString(ConstraintNameFormat));

        foreach (var interfaceType in type.AllInterfaces)
            compatibleTypes.Add(interfaceType.ToDisplayString(ConstraintNameFormat));

        compatibleTypes.Sort(StringComparer.Ordinal);
        return new TypeFacts(
            type.IsValueType,
            type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
            type.IsUnmanagedType,
            HasParameterlessConstructor(type),
            string.Join(";", compatibleTypes),
            type.ToDisplayString(ConstraintNameFormat));
    }

    private static bool HasParameterlessConstructor(ITypeSymbol type)
    {
        var constructor = (type as INamedTypeSymbol)?.InstanceConstructors
            .FirstOrDefault(static x => x.Parameters.Length == 0);

        // A type with required members only satisfies new() when that constructor sets them (CS9040).
        if (HasRequiredMembers(type) && !SetsRequiredMembers(constructor)) return false;
        if (type.IsValueType) return true;

        return !type.IsAbstract && constructor is { DeclaredAccessibility: Accessibility.Public };
    }

    private static bool HasRequiredMembers(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var member in current.GetMembers())
                if (member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })
                    return true;

        return false;
    }

    private static bool SetsRequiredMembers(IMethodSymbol? constructor) =>
        constructor is not null &&
        constructor.GetAttributes().Any(static x => x.AttributeClass!.Name == "SetsRequiredMembersAttribute");

    /// <summary>True when a message type implements both <c>IRequest</c> and <c>IRequest&lt;T&gt;</c> — an invalid, ambiguous request.</summary>
    private static bool ImplementsBothRequestInterfaces(ITypeSymbol message)
    {
        var hasVoid = false;
        var hasResponse = false;
        foreach (var interfaceType in message.AllInterfaces)
            if (IsMediatorType(interfaceType.OriginalDefinition, "IRequest")) hasVoid = true;
            else if (IsMediatorType(interfaceType.OriginalDefinition, "IRequest`1")) hasResponse = true;

        return hasVoid && hasResponse;
    }

    /// <summary>
    /// True when a message type implements more than one closed response interface of the given definition (<c>IRequest`1</c> / <c>IStreamRequest`1</c>).
    /// </summary>
    private static bool ImplementsMultipleResponseInterfaces(ITypeSymbol message, string responseInterfaceMetadataName)
    {
        string? firstResponse = null;
        foreach (var interfaceType in message.AllInterfaces)
        {
            if (!IsMediatorType(interfaceType.OriginalDefinition, responseInterfaceMetadataName))
                continue;

            var response = interfaceType.TypeArguments[0].ToDisplayString(FullyQualifiedNameFormat);
            if (firstResponse is null)
            {
                firstResponse = response;
                continue;
            }

            if (response != firstResponse) return true;
        }

        return false;
    }

    /// <summary>
    /// True when a top-level <c>typeof(type)</c> in generated consumer code can reference the type — i.e. it and all containing types are public or
    /// internal, and none is file-local.
    /// </summary>
    private static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol type, IAssemblySymbol generatedAssembly)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal) return false;

            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    break;
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                    if (!SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, generatedAssembly) &&
                        !current.ContainingAssembly.GivesAccessTo(generatedAssembly))
                        return false;

                    break;
                case Accessibility.NotApplicable:
                case Accessibility.Private:
                case Accessibility.ProtectedAndInternal:
                case Accessibility.Protected:
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Accessibility check for arbitrary message/response types (closed generics, arrays, …). A nested type is only as accessible as the type arguments of
    /// the types it is nested in, as <c>Box&lt;Secret&gt;.Message</c> is.
    /// </summary>
    private static bool TypeAccessibleFromGeneratedCode(ITypeSymbol type, IAssemblySymbol generatedAssembly) => type switch
    {
        INamedTypeSymbol named =>
            IsAccessibleFromGeneratedCode(named, generatedAssembly) &&
            named.TypeArguments.All(x => TypeAccessibleFromGeneratedCode(x, generatedAssembly)) &&
            (named.ContainingType is null || TypeAccessibleFromGeneratedCode(named.ContainingType, generatedAssembly)),
        IArrayTypeSymbol array => TypeAccessibleFromGeneratedCode(array.ElementType, generatedAssembly),
        _ => true
    };

    /// <summary>
    /// True when generated code can write <paramref name="type" /> at all, including every type it is built from. A call site can mention more than a
    /// handler declaration can: a type parameter of the enclosing method, a type reachable only through an extern alias, which <c>global::</c> cannot name,
    /// or a private type argument of a generic containing type.
    /// </summary>
    internal static bool NameableFromGeneratedCode(ITypeSymbol type, Compilation compilation) => type switch
    {
        ITypeParameterSymbol or IErrorTypeSymbol => false,
        INamedTypeSymbol named =>
            IsAccessibleFromGeneratedCode(named, compilation.Assembly) &&
            !RequiresExternAlias(named.ContainingAssembly, compilation) &&
            named.TypeArguments.All(x => NameableFromGeneratedCode(x, compilation)) &&
            (named.ContainingType is null || NameableFromGeneratedCode(named.ContainingType, compilation)),
        IArrayTypeSymbol array => NameableFromGeneratedCode(array.ElementType, compilation),
        _ => true
    };

    internal static bool RequiresExternAlias(IAssemblySymbol assembly, Compilation compilation) =>
        compilation.GetMetadataReference(assembly) is { Properties.Aliases: { IsEmpty: false } aliases } &&
        !aliases.Contains("global");
}