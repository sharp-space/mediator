// Two MediatorBuilderTests change MediatorRuntime's process-global list of generated assemblies, restoring it in a
// finally: one empties it, one adds to it. Disabling test parallelization keeps any concurrent AddMediator call from
// observing that transient state. The suite is small, so the cost is negligible.

using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]