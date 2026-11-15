using System.Runtime.CompilerServices;

namespace Sharp.Mediator.LibraryFixtures;

/// <summary>
/// A stream no other assembly can name, so no generator but this one closes middleware over it: the cross-assembly gap that startup closes with
/// MakeGenericType under the JIT and reports under Native AOT.
/// </summary>
internal sealed record InternalLibraryCount(int Count) : IStreamRequest<int>;

internal sealed class InternalLibraryCountHandler : IStreamRequestHandler<InternalLibraryCount, int>
{
    public async IAsyncEnumerable<int> Handle(
        InternalLibraryCount request,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        for (var i = 0; i < request.Count; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }
}