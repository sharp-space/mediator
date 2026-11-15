using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sharp.Mediator.SourceGenerator.Analysis;

namespace Sharp.Mediator.SourceGenerator.Generation;

/// <summary>Category a discovered handler dispatches.</summary>
internal enum MessageKind
{
    VoidRequest,
    Request,
    Event,
    Stream
}

/// <summary>Why an open-generic middleware cannot be emitted as a closed registration.</summary>
[Flags]
internal enum MiddlewareIssue
{
    None = 0,
    Inaccessible = 1,
    UnsupportedOpenGenericShape = 2
}

/// <summary>
/// Equatable snapshot of a source location. Models must stay value-equatable for incremental caching, so they cannot hold <see cref="Location" /> (which
/// compares by reference through its syntax tree).
/// </summary>
internal readonly record struct LocationSnapshot(string FilePath, int Start, int Length)
{
    public static LocationSnapshot None => new("", 0, 0);

    public static LocationSnapshot From(Location location) =>
        location.SourceTree is null
            ? None
            : new LocationSnapshot(location.SourceTree.FilePath, location.SourceSpan.Start, location.SourceSpan.Length);

    /// <summary>
    /// Rebinds to the compilation's own syntax tree, so <c>#pragma</c> and <c>.editorconfig</c> severity settings, which an external location escapes,
    /// apply. Every diagnostic is reported on a declaration in the compilation.
    /// </summary>
    public Location ToLocation(Compilation compilation)
    {
        var filePath = FilePath;
        return Location.Create(compilation.SyntaxTrees.First(x => x.FilePath == filePath), new TextSpan(Start, Length));
    }
}

/// <summary>
/// The constraint requirements of one middleware type parameter. <see cref="ConstraintTypesJoined" /> holds constraints with no type-parameter
/// references (checked by exact match); <see cref="SubstitutableConstraintTypesJoined" /> holds constraints that reference the middleware's own type
/// parameters by name (e.g. <c>IMessage&lt;TResponse&gt;</c>, or <c>TMessage</c> itself) — checked after substituting each parameter name with the
/// candidate's closed type.
/// </summary>
internal readonly record struct TypeParameterRequirements(
    bool RequiresReferenceType,
    bool RequiresValueType,
    bool RequiresUnmanagedType,
    bool RequiresParameterlessConstructor,
    string ConstraintTypesJoined,
    string SubstitutableConstraintTypesJoined
);

/// <summary>
/// Facts about a message/response type needed to evaluate middleware constraints against it. <see cref="CompatibleTypesJoined" /> is the type itself,
/// its base chain, and all implemented interfaces (fully-qualified, Ordinal-sorted, ';'-joined) — the set of constraint types the type satisfies. Names
/// here use the constraint-matching format, so identical types compare equal whatever their tuple element names.
/// </summary>
internal readonly record struct TypeFacts(
    bool IsValueType,
    bool IsNullableValueType,
    bool IsUnmanagedType,
    bool HasParameterlessConstructor,
    string CompatibleTypesJoined,
    string ConstraintName
);

/// <summary>
/// A single handler-interface implementation discovered in the compilation. One handler class that implements several handler interfaces yields one
/// <see cref="HandlerInfo" /> per interface. All members are equatable values so the record works with incremental caching.
/// </summary>
internal readonly record struct HandlerInfo(
    string HandlerFullyQualifiedName,
    MessageKind Kind,
    string MessageFullyQualifiedName,
    string MessageMetadataName,
    string? ResponseFullyQualifiedName,
    bool MessageImplementsBothRequestInterfaces,
    bool MessageImplementsMultipleResponseInterfaces,
    string ServiceInterfaceFullyQualifiedName,
    bool HandlerAccessible,
    bool MessageAccessible,
    bool MessageIsInterface,
    TypeFacts MessageFacts,
    TypeFacts ResponseFacts,
    LocationSnapshot HandlerLocation
);

/// <summary>
/// An open-generic middleware definition: declared in the compilation, or found in the metadata of a referenced assembly that references Sharp.Mediator
/// and accessible to generated code. Closed middleware never reaches the generator — its closed interfaces are registered at runtime.
/// <see cref="HasVariantConstraint" /> marks one with a variant interface among its constraints, which a message can meet through a conversion the names
/// of its types do not show (see <see cref="Applicability" />). <see cref="AssemblyName" /> names the assembly that declares it, with which generated
/// code records the middleware it closes (see <see cref="ReferenceAnalysis" />).
/// </summary>
internal readonly record struct MiddlewareInfo(
    bool IsStream,
    int Arity,
    string MatchTypeFullyQualifiedName,
    string OpenBaseFullyQualifiedName,
    MiddlewareIssue Issues,
    string TypeParameterNamesJoined,
    TypeParameterRequirements MessageRequirements,
    TypeParameterRequirements ResponseRequirements,
    bool HasVariantConstraint,
    string AssemblyName,
    LocationSnapshot Location
);

/// <summary>
/// A message whose handler a referenced assembly declares, which this compilation's middleware closer also covers. <see cref="ClosedElsewhere" /> names
/// the referenced middleware that a generator which saw both already closes over it.
/// </summary>
internal readonly record struct ClosingTarget(HandlerInfo Handler, EquatableArray<string> ClosedElsewhere);

/// <summary>What the generator takes from referenced assemblies: the middleware it can close, and the messages to close over.</summary>
internal readonly record struct ReferenceScan(EquatableArray<MiddlewareInfo> Middlewares, EquatableArray<ClosingTarget> Messages);

/// <summary>
/// A closed middleware interface no pipeline resolves, because its message dispatches through a different shape. Diagnostic-only: closed middleware
/// registers at runtime and never reaches source emission.
/// </summary>
internal readonly record struct UnreachableMiddlewareInfo(
    string MiddlewareFullyQualifiedName,
    string ServiceInterfaceFullyQualifiedName,
    string MessageFullyQualifiedName,
    LocationSnapshot Location
);

/// <summary>A concrete generic handler, which neither the generator nor <c>AddHandlers</c> can register. Diagnostic-only.</summary>
internal readonly record struct GenericHandlerInfo(string HandlerFullyQualifiedName, LocationSnapshot Location);

/// <summary>
/// Value-equatable wrapper for <see cref="ImmutableArray{T}" /> so provider outputs compare by content (a raw ImmutableArray compares by underlying
/// array reference, defeating incremental caching).
/// </summary>
internal readonly struct EquatableArray<T>(ImmutableArray<T> values) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    public ImmutableArray<T> Values => values.IsDefault ? ImmutableArray<T>.Empty : values;

    public bool Equals(EquatableArray<T> other)
    {
        var left = Values;
        var right = other.Values;
        if (left.Length != right.Length) return false;

        for (var i = 0; i < left.Length; i++)
            if (!left[i].Equals(right[i]))
                return false;

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode() => Enumerable.Aggregate(Values, 0, (current, value) => unchecked((current * 31) + value.GetHashCode()));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}