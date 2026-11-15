namespace Sharp.Mediator.Validation;

/// <summary>Mediator-builder extensions that wire up the validation middleware.</summary>
public static class ValidationExtensions
{
    private const string TypedValidationModeKey = "typed-validation-mode";
    private const string ThrowingMode = "throw";
    private const string ResponseMode = "response";

    private const string TypedModeConflictMessage =
        "AddValidationMiddleware()/AddThrowingResponseValidationMiddleware() and " +
        "AddResponseValidationMiddleware() are mutually exclusive for response-returning requests. " +
        "Choose one typed validation policy.";

    extension(MediatorBuilder builder)
    {
        /// <summary>
        /// Convenience registration that validates void requests, response-returning requests, and stream requests, and throws <c>ValidationException</c> on
        /// failure. Equivalent to combining <see cref="AddRequestValidationMiddleware" />, <see cref="AddThrowingResponseValidationMiddleware" />, and
        /// <see cref="AddStreamValidationMiddleware" />. Cannot be combined with <see cref="AddResponseValidationMiddleware" /> on one service collection.
        /// </summary>
        public MediatorBuilder AddValidationMiddleware()
        {
            UseTypedValidationMode(builder, ThrowingMode);
            builder.AddMiddleware(typeof(ValidationMiddleware<>));
            builder.AddMiddleware(typeof(ValidationMiddleware<,>));
            builder.AddMiddleware(typeof(ValidationStreamMiddleware<,>));
            return builder;
        }

        /// <summary>
        /// Validates stream requests before the stream starts, and throws <c>ValidationException</c> on failure at the first <c>MoveNextAsync</c>. Other requests
        /// and events are not affected.
        /// </summary>
        public MediatorBuilder AddStreamValidationMiddleware() =>
            builder.AddMiddleware(typeof(ValidationStreamMiddleware<,>));

        /// <summary>
        /// Validates void <c>IRequest</c> messages and throws <c>ValidationException</c> on failure. Response-returning requests and events are not affected.
        /// </summary>
        public MediatorBuilder AddRequestValidationMiddleware() =>
            builder.AddMiddleware(typeof(ValidationMiddleware<>));

        /// <summary>
        /// Validates response-returning <c>IRequest&lt;TResponse&gt;</c> messages and throws <c>ValidationException</c> on failure. Void requests and events are
        /// not affected. Cannot be combined with <see cref="AddResponseValidationMiddleware" /> on one service collection.
        /// </summary>
        public MediatorBuilder AddThrowingResponseValidationMiddleware()
        {
            UseTypedValidationMode(builder, ThrowingMode);
            return builder.AddMiddleware(typeof(ValidationMiddleware<,>));
        }

        /// <summary>
        /// Validates response-returning requests only when the response implements <c>IResponse</c> and has a public parameterless constructor; failures return
        /// an Invalid response. Void requests, other response types, and events are not affected, so combine this with
        /// <see cref="AddRequestValidationMiddleware" /> when void requests should also be validated. Cannot be combined with
        /// <see cref="AddValidationMiddleware" /> or <see cref="AddThrowingResponseValidationMiddleware" /> on one service collection.
        /// </summary>
        public MediatorBuilder AddResponseValidationMiddleware()
        {
            UseTypedValidationMode(builder, ResponseMode);
            return builder.AddMiddleware(typeof(ResponseValidationMiddleware<,>));
        }
    }

    private static void UseTypedValidationMode(MediatorBuilder builder, string mode) =>
        builder.WithExclusiveOption(TypedValidationModeKey, mode, TypedModeConflictMessage);
}