// ReSharper disable StaticMemberInGenericType — intentional: each closed slot type (RequestSlot<Ping,int>, …) needs a
// static index of its own, which is the point of the design.

namespace Sharp.Mediator.Bindings;

// Per-type cache-slot indices. Value-type closed generics get dedicated instantiations, so the static field can be
// reached directly; reference-type generic sharing locates the exact closed static storage through the runtime's
// generic dictionary. Neither depends on how many messages exist. A closed type takes its slot the first time it is
// touched, so the numbering only covers messages this process has dispatched and startup does no work per message.
// Each mediator instance indexes its chain array by these, so the cache's lifetime matches the mediator's.

/// <summary>
/// Cache-slot index for a request that returns a response, and whether <typeparamref name="TRequest" /> is known to declare a handler for that response.
/// </summary>
internal static class RequestSlot<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    internal static readonly int Index = MediatorRuntime.NextSlot();

    // Set once a dispatch has found TRequest's own route. Nothing is nearer to an instance of exactly TRequest, and no
    // later assembly replaces a declared type's route, so it is never cleared. Kept beside the index, so shared generic
    // code finds both with one lookup.
    internal static bool Declared;
}

/// <summary>Cache-slot index for a void request, and whether <typeparamref name="TRequest" /> is known to declare a handler.</summary>
internal static class VoidRequestSlot<TRequest> where TRequest : IRequest
{
    internal static readonly int Index = MediatorRuntime.NextSlot();

    // As RequestSlot<TRequest, TResponse>.Declared.
    internal static bool Declared;
}

/// <summary>Cache-slot index for an event, and whether an event of exactly <typeparamref name="TEvent" /> reaches its own level alone.</summary>
internal static class EventSlot<TEvent> where TEvent : IEvent
{
    internal static readonly int Index = MediatorRuntime.NextSlot();

    // Process-wide, as the levels are: they come from the declarations of every assembly, not from a container. Set and
    // cleared by MessageRoutes, since an assembly that adds resolvers can declare another level. Kept beside the index,
    // so shared generic code finds both with one lookup.
    internal static bool Complete;
}

/// <summary>
/// Cache-slot index for a stream request, and whether <typeparamref name="TRequest" /> is known to declare a handler for that item type.
/// </summary>
internal static class StreamRequestSlot<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    internal static readonly int Index = MediatorRuntime.NextSlot();

    // As RequestSlot<TRequest, TResponse>.Declared.
    internal static bool Declared;
}