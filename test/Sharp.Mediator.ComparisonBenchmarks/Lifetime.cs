#if SCOPED_LIFETIME
using global::Mediator;
using Microsoft.Extensions.DependencyInjection;

// Mediator's generator reads this attribute by its simple name: written through a namespace alias it is ignored without
// a diagnostic and the generated code stays Singleton. Setup.Martin checks the result for that reason.
[assembly: MediatorOptions(ServiceLifetime = ServiceLifetime.Scoped)]
#endif