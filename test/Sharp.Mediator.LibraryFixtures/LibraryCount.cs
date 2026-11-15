using System.Runtime.CompilerServices;

namespace Sharp.Mediator.LibraryFixtures;

public sealed record LibraryCount(int Count) : IStreamRequest<int>;

public sealed class LibraryCountHandler : IStreamRequestHandler<LibraryCount, int>
{
    public async IAsyncEnumerable<int> Handle(
        LibraryCount request,
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