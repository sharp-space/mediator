using System;
using System.Text;

namespace Sharp.Mediator.SourceGenerator.Generation;

internal static partial class Emitter
{
    /// <summary>True when closing the middleware over this message (and, for arity-2, this response) satisfies every generic constraint.</summary>
    internal static bool SatisfiesConstraints(MiddlewareInfo middleware, TypeFacts message, TypeFacts response)
    {
        var parameterNames = middleware.TypeParameterNamesJoined.Split(';');
        string[] closedArguments = middleware.Arity == 1
            ? [message.ConstraintName]
            : [message.ConstraintName, response.ConstraintName];

        return Satisfies(message, middleware.MessageRequirements, parameterNames, closedArguments)
            && (middleware.Arity == 1 || Satisfies(response, middleware.ResponseRequirements, parameterNames, closedArguments));
    }

    internal static bool Satisfies(
        TypeFacts candidate,
        TypeParameterRequirements requirements,
        string[] parameterNames,
        string[] closedArguments
    )
    {
        // `notnull` is deliberately ignored: violating it is CS8714, a nullable warning that generated code disables.
        if (requirements.RequiresReferenceType && candidate.IsValueType) return false;
        if (requirements.RequiresValueType && (!candidate.IsValueType || candidate.IsNullableValueType)) return false;
        if (requirements.RequiresUnmanagedType && !candidate.IsUnmanagedType) return false;
        if (requirements.RequiresParameterlessConstructor && !candidate.HasParameterlessConstructor) return false;

        var compatibleTypes = candidate.CompatibleTypesJoined.Split(';');
        if (requirements.ConstraintTypesJoined.Length > 0)
        {
            foreach (var required in requirements.ConstraintTypesJoined.Split(';'))
                if (required.Length > 0 && Array.IndexOf(compatibleTypes, required) < 0)
                    return false;
        }

        if (requirements.SubstitutableConstraintTypesJoined.Length <= 0) return true;

        foreach (var required in requirements.SubstitutableConstraintTypesJoined.Split(';'))
        {
            if (required.Length == 0) continue;

            var substituted = SubstituteTypeParameters(required, parameterNames, closedArguments);
            if (Array.IndexOf(compatibleTypes, substituted) < 0)
                return false;
        }

        return true;
    }

    /// <summary>Replaces standalone type-parameter name tokens (e.g. <c>TResponse</c>) with the candidate's closed type names.</summary>
    internal static string SubstituteTypeParameters(string constraint, string[] parameterNames, string[] closedArguments)
    {
        var stringBuilder = new StringBuilder(constraint.Length + 32);
        var start = 0;
        for (var i = 0; i <= constraint.Length; i++)
        {
            var atBoundary = i == constraint.Length || !IsIdentifierCharacter(constraint[i]);
            if (!atBoundary) continue;

            if (i > start)
            {
                var token = constraint.Substring(start, i - start);
                var parameterIndex = Array.IndexOf(parameterNames, token);
                stringBuilder.Append(parameterIndex >= 0 && parameterIndex < closedArguments.Length
                    ? closedArguments[parameterIndex]
                    : token);
            }

            if (i < constraint.Length) stringBuilder.Append(constraint[i]);

            start = i + 1;
        }

        return stringBuilder.ToString();
    }

    internal static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '@';

    // The last name outside every type argument list: Wrapper<Item> is Wrapper, and Box<Secret>.Message is Message.
}