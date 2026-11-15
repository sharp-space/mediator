namespace Sharp.Mediator.Responses;

/// <summary>Result classification for an <see cref="IResponse" />.</summary>
public enum ResponseStatus
{
    /// <summary>The response was not initialized through a factory or explicit status assignment.</summary>
    Unspecified = 0,

    /// <summary>The operation succeeded.</summary>
    Success = 1,

    /// <summary>The operation failed for a domain reason described by <see cref="IResponse.Error" />.</summary>
    Error = 2,

    /// <summary>The request was rejected because of validation failures in <see cref="IResponse.ValidationErrors" />.</summary>
    Invalid = 3,

    /// <summary>The requested resource was not found.</summary>
    NotFound = 4,

    /// <summary>The caller is not authenticated (HTTP 401).</summary>
    Unauthenticated = 5,

    /// <summary>The caller is authenticated but not permitted (HTTP 403).</summary>
    Forbidden = 6
}