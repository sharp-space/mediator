namespace Sharp.Mediator;

/// <summary>Entry point for dispatching requests, events, and stream requests.</summary>
public interface IMediator : IRequester, IPublisher;