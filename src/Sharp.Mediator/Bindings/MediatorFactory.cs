using System.ComponentModel;

// ReSharper disable UnusedMember.Global — only the registration code the generator emits calls these methods.

namespace Sharp.Mediator.Bindings;

/// <summary>
/// Factory methods invoked by source-generated code to instantiate the internal routes. Each call site (<c>CreateRequestRoute&lt;Ping, int&gt;</c> …)
/// statically roots the value-type-specialized dispatch code under Native AOT, which a closed <c>typeof()</c> alone would not.
/// </summary>
/// <remarks>
/// Infrastructure for the Sharp.Mediator source generator. Not intended for direct use.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MediatorFactory
{
    /// <summary>Creates the runtime-type route for a request that returns a response.</summary>
    public static object CreateRequestRoute<TRequest, TResponse>() where TRequest : IRequest<TResponse> =>
        new ResponseRequestRoute<TRequest, TResponse>();

    /// <summary>Creates the runtime-type route for a void request.</summary>
    public static object CreateVoidRequestRoute<TRequest>() where TRequest : IRequest =>
        new VoidRequestRoute<TRequest>();

    /// <summary>Creates the runtime-type route for an event.</summary>
    public static object CreateEventRoute<TEvent>() where TEvent : IEvent => new EventRoute<TEvent>();

    /// <summary>Creates the runtime-type route for a stream request.</summary>
    public static object CreateStreamRoute<TRequest, TResponse>() where TRequest : IStreamRequest<TResponse> =>
        new StreamRoute<TRequest, TResponse>();
}