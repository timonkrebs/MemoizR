namespace MemoizR;

// Phase 1 of the Causality Trigger Clock (CTC, issue #39): an immutable map from signal id to
// that signal's trigger value (a per-signal version counter, bumped on every value-changing
// Set). A signal's stamp is the single entry { #id: trigger }; a derived node's stamp is the
// join of the stamps it observed on its tracked source reads during its last completed
// evaluation -- the set of signal versions its current state reflects ({ #6: { 2:7, 3:2 } } in
// the issue's notation).
//
// Two stamps are "consistent" when they agree on every signal they both track -- the
// glitch-freedom check a distributed consumer runs across its inputs' stamps. Within one
// process the locking layers already guarantee glitch-freedom; stamps are the preparation for
// verifying it where locks cannot reach (distributed graphs).
//
// Capture discipline (enforced by the graph integration, relied on by consumers): a stamp is
// taken from the same volatile box publication as the value it describes, so a (value, stamp)
// pair is never torn, and a node's published stamp never OVER-claims -- it records exactly the
// triggers of the source publications its value was computed from, never newer ones. An
// over-claim could make a distributed consistency check pass against a write the value does not
// reflect; the conservative direction (a node that skips recomputing because a parent's value
// came out unchanged keeps its older stamp) only costs a spurious recheck.
//
// Naive dictionary representation by design in phase 1; the ITC-inspired space-efficient
// encoding and serialization are phase 2, reset resilience is phase 3.
internal sealed class CausalityStamp : IEquatable<CausalityStamp>
{
    public static CausalityStamp Empty { get; } = new(new Dictionary<int, long>());

    // Never mutated after construction; the stamp is published across threads by reference.
    private readonly Dictionary<int, long> triggers;

    private CausalityStamp(Dictionary<int, long> triggers)
    {
        this.triggers = triggers;
    }

    public static CausalityStamp ForSignal(int signalId, long trigger) => new(new() { [signalId] = trigger });

    public IReadOnlyDictionary<int, long> Triggers => triggers;

    public bool TryGetTrigger(int signalId, out long trigger) => triggers.TryGetValue(signalId, out trigger);

    // Least upper bound: pointwise max over the union of tracked signals. Associative,
    // commutative and idempotent, so the accumulation order across parallel source reads
    // (structured-concurrency children) cannot affect the result.
    public CausalityStamp Join(CausalityStamp other)
    {
        if (triggers.Count == 0)
        {
            return other;
        }
        if (other.triggers.Count == 0)
        {
            return this;
        }

        var merged = new Dictionary<int, long>(triggers);
        foreach (var (id, trigger) in other.triggers)
        {
            merged[id] = merged.TryGetValue(id, out var existing) && existing >= trigger ? existing : trigger;
        }
        return new(merged);
    }

    public static CausalityStamp JoinAll(IEnumerable<CausalityStamp> stamps)
    {
        var result = Empty;
        foreach (var stamp in stamps)
        {
            result = result.Join(stamp);
        }
        return result;
    }

    // The glitch detector: two stamps are consistent when they agree on every signal both
    // track. Disjoint stamps are trivially consistent (no shared causality to disagree about).
    public bool IsConsistentWith(CausalityStamp other)
    {
        var (smaller, larger) = triggers.Count <= other.triggers.Count
            ? (triggers, other.triggers)
            : (other.triggers, triggers);

        foreach (var (id, trigger) in smaller)
        {
            if (larger.TryGetValue(id, out var otherTrigger) && otherTrigger != trigger)
            {
                return false;
            }
        }
        return true;
    }

    public bool Equals(CausalityStamp? other)
    {
        if (other is null || triggers.Count != other.triggers.Count)
        {
            return false;
        }

        foreach (var (id, trigger) in triggers)
        {
            if (!other.triggers.TryGetValue(id, out var otherTrigger) || otherTrigger != trigger)
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as CausalityStamp);

    public override int GetHashCode()
    {
        // Order-independent combine so equal stamps hash equal regardless of insertion order.
        var hash = 0;
        foreach (var (id, trigger) in triggers)
        {
            hash ^= HashCode.Combine(id, trigger);
        }
        return hash;
    }

    public override string ToString() =>
        "{" + string.Join(", ", triggers.OrderBy(t => t.Key).Select(t => $"#{t.Key}: {t.Value}")) + "}";
}
