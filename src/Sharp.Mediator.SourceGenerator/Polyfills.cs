// Polyfills so C# records (init-only setters) compile on netstandard2.0, which the generator must target.
// ReSharper disable once CheckNamespace — IsExternalInit must live in System.Runtime.CompilerServices to
// enable init setters.

namespace System.Runtime.CompilerServices;

internal static class IsExternalInit;