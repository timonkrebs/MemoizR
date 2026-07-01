// A user-declared type living inside a framework collection namespace. The Sendable checkers
// trust the known framework collection DEFINITIONS, not these namespaces as strings -- this type
// must go through the ordinary structural walk (and fail it: the field is writable shared
// state). Lives in its own file because the namespace differs from the test project's.
namespace System.Collections.Concurrent
{
    internal sealed class HomegrownConcurrentCache
    {
        public int Hits;
    }
}
