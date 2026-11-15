namespace Sharp.Mediator.SourceGenerator.Analysis;

internal static class SymbolNames
{
    // A name as a diagnostic shows it in full: without the global:: alias, which only generated code needs, but with
    // its namespace and type arguments, so two types of one simple name stay apart.
    internal static string DisplayName(string fullyQualifiedName) => fullyQualifiedName.Replace("global::", string.Empty);

    internal static string SimpleName(string fullyQualifiedName)
    {
        // Without the alias first, since a type in the global namespace has no dot to cut it at.
        fullyQualifiedName = DisplayName(fullyQualifiedName);
        var start = 0;
        var end = fullyQualifiedName.Length;
        var depth = 0;
        for (var i = 0; i < fullyQualifiedName.Length; i++)
            switch (fullyQualifiedName[i])
            {
                case '<':
                    if (depth++ == 0) end = i;

                    break;
                case '>':
                    depth--;
                    break;
                case '.' when depth == 0:
                    start = i + 1;
                    end = fullyQualifiedName.Length;
                    break;
            }

        return fullyQualifiedName.Substring(start, end - start);
    }
}