namespace Sharp.Mediator.Responses;

/// <summary>
/// A page of items, and the number of items across every page when it is known. Return it as a <c>Response&lt;Page&lt;T&gt;&gt;</c>, which serializes it
/// as <c>{ "items": [...], "count": n }</c> on success.
/// </summary>
/// <param name="Items">The items of this page.</param>
/// <param name="Count">The number of items across every page, or null when it is not known.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, long? Count = null);

/// <summary>
/// A page of items and the cursor of the page after it. Return it as a <c>Response&lt;CursorPage&lt;T&gt;&gt;</c>, which serializes it as
/// <c>{ "items": [...], "nextCursor": "..." }</c> on success.
/// </summary>
/// <param name="Items">The items of this page.</param>
/// <param name="NextCursor">An opaque cursor for the next page, or null when there are no more items.</param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor = null);

/// <summary>
/// The id of what an operation created or found. Return it as a <c>Response&lt;Identifier&lt;T&gt;&gt;</c>, which serializes it as
/// <c>{ "id": ... }</c> on success.
/// </summary>
/// <param name="Id">The id.</param>
public sealed record Identifier<T>(T Id) where T : notnull;