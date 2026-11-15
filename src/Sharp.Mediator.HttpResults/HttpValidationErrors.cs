using Sharp.Validator.Result;

namespace Sharp.Mediator.HttpResults;

/// <summary>
/// The body of a 422 response, <c>{ "errors": [ ... ] }</c>. An object, so that a field can be added later without breaking a client, and its entries are
/// this package's own, so that the HTTP format does not change when <see cref="ValidationError" /> does.
/// </summary>
/// <param name="Errors">One entry per validation error, in the order the response holds them.</param>
public sealed record HttpValidationErrors(IReadOnlyList<HttpValidationError> Errors);

/// <summary>One entry of <see cref="HttpValidationErrors" />: <c>{ "propertyName", "message", "code", "severity" }</c>.</summary>
/// <param name="PropertyName">The property the error is about.</param>
/// <param name="Message">What is wrong with it, when the validator gave a message.</param>
/// <param name="Code">The validator's code for the error, when it gave one.</param>
/// <param name="Severity">
/// The name of the entry's <see cref="ValidationErrorSeverity" />: Error, Warning or Information. A string, so it reads the same whichever
/// Sharp.Validator version, and whatever enum converter, the application uses.
/// </param>
public sealed record HttpValidationError(string PropertyName, string? Message, string? Code, string Severity);