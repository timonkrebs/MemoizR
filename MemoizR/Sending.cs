namespace MemoizR;

/// <summary>
/// Transfer semantics for a non-Sendable value (the analog of Swift's <c>sending</c> parameters,
/// SE-0430 / region-based isolation, SE-0414): the sender promises to stop touching the value,
/// so ONE receiver on another flow may own it -- disjointness by handover instead of
/// immutability. The wrapper is <see cref="SendableAttribute">[Sendable]</see>, so strict mode
/// accepts a <c>Sending&lt;T&gt;</c> signal/memo value even when <typeparamref name="T"/> is not
/// Sendable itself.
///
/// The runtime enforces SINGLE consumption (<see cref="Receive"/> throws on the second call and
/// drops the reference after the first), and the MZR005 analyzer flags uses of the transferred
/// variable after the transfer in the same method. Unlike Swift's compiler-checked regions this
/// is a best-effort contract: a sender that keeps an alias through another path can still race
/// -- the wrapper narrows the hole, it cannot close it (documented in ADR 0003).
/// </summary>
[Sendable]
public sealed class Sending<T>
{
    private T? value;
    private int received;

    public Sending(T value)
    {
        this.value = value;
    }

    /// <summary>
    /// Takes ownership of the transferred value. Callable exactly once: a second receive would
    /// mean two owners, which is the aliasing this type exists to prevent. The stored reference
    /// is dropped so the wrapper does not keep the transferred object alive.
    /// </summary>
    public T Receive()
    {
        if (Interlocked.Exchange(ref received, 1) != 0)
        {
            throw new InvalidOperationException(
                "This Sending<T> was already received. A transferred value has exactly one owner: " +
                "receive it once and pass the received value on, or transfer it again explicitly.");
        }

        var transferred = value!;
        value = default;
        return transferred;
    }
}

/// <summary>Factory sugar so the type argument is inferred: <c>Sending.Transfer(list)</c>.</summary>
public static class Sending
{
    public static Sending<T> Transfer<T>(T value)
    {
        return new(value);
    }
}
