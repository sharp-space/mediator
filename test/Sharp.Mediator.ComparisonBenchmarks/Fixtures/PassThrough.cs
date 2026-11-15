using MartinMediator = Mediator;
using MediatRMediator = MediatR;

namespace Sharp.Mediator.ComparisonBenchmarks.Fixtures;

// The messages and handlers are generated into obj/Messages.<MessageCount>.g.cs, one namespace for each library. These
// are the pass-through steps MiddlewareBenchmarks puts in front of the handlers.

public sealed class SharpPassThrough<TMessage, TResponse> : IMiddleware<TMessage, TResponse>
    where TMessage : IMessage<TResponse>
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandle<TMessage, TResponse> next,
        CancellationToken cancellationToken
    ) => next(message, cancellationToken);
}

public sealed class MartinPassThrough<TMessage, TResponse> : MartinMediator.IPipelineBehavior<TMessage, TResponse>
    where TMessage : MartinMediator.IMessage
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MartinMediator.MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken
    ) => next(message, cancellationToken);
}

public sealed class MediatRPassThrough<TRequest, TResponse> : MediatRMediator.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(
        TRequest request,
        MediatRMediator.RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken
    ) => next(cancellationToken);
}