namespace MemoizR;

// The Causality Trigger Clock stamp (CTC, issue #39): an immutable map from signal id to that
// signal's trigger value (a per-signal version counter, bumped on every value-changing Set),
// tagged with the incarnation epoch of the context that produced it. A signal's stamp is the
// single entry { #id: trigger }; a derived node's stamp is the join of the stamps it observed
// on its tracked source reads during its last completed evaluation -- the set of signal
// versions its current state reflects ({ #6: { 2:7, 3:2 } } in the issue's notation).
//
// Two stamps are "consistent" when they agree on every signal they both track -- the
// glitch-freedom check a distributed consumer runs across its inputs' stamps. Within one
// process the locking layers already guarantee glitch-freedom; stamps make that property
// checkable where locks cannot reach (distributed graphs).
//
// Reset resilience: ids and triggers restart when a process (and so its Context) restarts, so
// a recreated graph reissues (id, trigger) pairs that already escaped over the wire. Every
// stamp therefore carries the random nonzero EPOCH of its context incarnation: stamps from
// different incarnations are never equal, never report consistency, and refuse to join --
// observing an epoch change is the sync layer's signal to discard its stale state for that
// peer, not to merge. The empty stamp claims nothing and is epoch-agnostic (epoch 0).
//
// Capture discipline (enforced by the graph integration, relied on by consumers): a stamp is
// taken from the same volatile box publication as the value it describes, so a (value, stamp)
// pair is never torn, and a node's published stamp never OVER-claims -- it records exactly the
// triggers of the source publications its value was computed from, never newer ones. An
// over-claim could make a distributed consistency check pass against a write the value does not
// reflect; the conservative direction (a node that skips recomputing because a parent's value
// came out unchanged keeps its older stamp) only costs a spurious recheck.
//
// Representation (inspired by Interval Tree Clocks): a canonical binary event tree over the id
// space [0, 2^spanBits), where the value stored at an id is the sum of the N fields along its
// path. The stored value encodes "untracked" as 0 and "tracked at trigger t" as t + 1, which
// turns the stamp into a total function with default 0 -- exactly the shape ITC event trees
// compress: uniform regions (untracked gaps, batches of fresh signals at trigger 0,
// lockstep-updated ranges) collapse into single leaves, and shared minimums are lifted into
// parents. Trees are persistent: Join reuses whole subtrees of its operands. The normal form
// (see MkNode/FromCanonicalTree) is unique per logical map, so structural equality is semantic
// equality and serialization is deterministic.
public sealed class CausalityStamp : IEquatable<CausalityStamp>
{
    public static CausalityStamp Empty { get; } = new(EventTree.Zero, 0, 0);

    private readonly EventTree root; // canonical (normal-form, trimmed) -- see MkNode/FromCanonicalTree
    private readonly int spanBits;   // root covers ids [0, 2^spanBits); 0 means just id 0

    // The incarnation that produced this stamp's observations: random and nonzero per Context,
    // 0 only on the empty stamp. Triggers are only comparable within one epoch.
    public long Epoch { get; }

    private CausalityStamp(EventTree root, int spanBits, long epoch)
    {
        this.root = root;
        this.spanBits = spanBits;
        Epoch = epoch;
    }

    private bool IsEmpty => root.IsLeaf && root.N == 0;

    internal static CausalityStamp ForSignal(int signalId, long trigger, long epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(signalId);
        ArgumentOutOfRangeException.ThrowIfNegative(trigger);
        ArgumentOutOfRangeException.ThrowIfZero(epoch);

        var bits = 0;
        while (signalId >> bits != 0)
        {
            bits++;
        }
        return new(EventTree.Singleton(signalId, bits, trigger + 1), bits, epoch);
    }

    // Diagnostic view; materialized per call (one entry per tracked signal).
    public IReadOnlyDictionary<int, long> Triggers
    {
        get
        {
            var triggers = new Dictionary<int, long>();
            root.CollectTracked(spanBits, 0, 0, triggers);
            return triggers;
        }
    }

    public bool TryGetTrigger(int signalId, out long trigger)
    {
        trigger = 0;
        if (signalId < 0 || signalId >> spanBits != 0)
        {
            return false;
        }

        var value = root.ValueAt(signalId, spanBits);
        if (value == 0)
        {
            return false;
        }
        trigger = value - 1;
        return true;
    }

    // Least upper bound: pointwise max over the union of tracked signals. Associative,
    // commutative and idempotent, so the accumulation order across parallel source reads
    // (structured-concurrency children) cannot affect the result. Joining across epochs throws:
    // triggers of different incarnations are incomparable, and a mismatch reaching a join means
    // the caller skipped the reset detection it owes (discard the stale side instead).
    public CausalityStamp Join(CausalityStamp other)
    {
        if (IsEmpty)
        {
            return other;
        }
        if (other.IsEmpty)
        {
            return this;
        }
        if (Epoch != other.Epoch)
        {
            throw new InvalidOperationException(
                "Causality stamps from different incarnation epochs cannot be joined: one side observed a peer that has since reset. Discard the stale stamp instead of merging it.");
        }

        var bits = Math.Max(spanBits, other.spanBits);
        var joined = EventTree.Join(root.Grow(spanBits, bits), other.root.Grow(other.spanBits, bits));
        return FromCanonicalTree(joined, bits, Epoch);
    }

    internal static CausalityStamp JoinAll(IEnumerable<CausalityStamp> stamps)
    {
        var result = Empty;
        foreach (var stamp in stamps)
        {
            result = result.Join(stamp);
        }
        return result;
    }

    // The glitch detector: two stamps are consistent when they agree on every signal both
    // track. Disjoint stamps are trivially consistent (no shared causality to disagree about);
    // stamps from different incarnation epochs never are (their triggers count different write
    // histories, so identical numbers are not agreement).
    public bool IsConsistentWith(CausalityStamp other)
    {
        if (IsEmpty || other.IsEmpty)
        {
            return true;
        }
        if (Epoch != other.Epoch)
        {
            return false;
        }

        var bits = Math.Max(spanBits, other.spanBits);
        return EventTree.Consistent(root.Grow(spanBits, bits), 0, other.root.Grow(other.spanBits, bits), 0);
    }

    // Causal dominance, the lattice order under Join: does `other` reflect every signal version
    // this stamp reflects -- each tracked signal also tracked by `other` with an at-least-as-new
    // trigger? Equal stamps dominate each other; the empty stamp claims nothing and is dominated
    // by everything. A sync layer uses this to order observations of one peer: an incoming
    // publication whose stamp is dominated by the already-known one is old news (late, duplicated
    // or reordered delivery) and can be dropped, and the undominated side of a glitched pair is
    // the one to wait for. Non-empty stamps of different incarnation epochs are incomparable
    // (false either way), matching IsConsistentWith.
    public bool IsDominatedBy(CausalityStamp other)
    {
        if (IsEmpty)
        {
            return true;
        }
        if (other.IsEmpty || Epoch != other.Epoch)
        {
            return false;
        }

        var bits = Math.Max(spanBits, other.spanBits);
        return EventTree.Leq(root.Grow(spanBits, bits), 0, other.root.Grow(other.spanBits, bits), 0);
    }

    public bool Equals(CausalityStamp? other)
    {
        if (other is null)
        {
            return false;
        }
        // Canonical form is unique per logical map, so structural equality decides.
        return Epoch == other.Epoch && spanBits == other.spanBits && EventTree.StructurallyEqual(root, other.root);
    }

    public override bool Equals(object? obj) => Equals(obj as CausalityStamp);

    public override int GetHashCode() => HashCode.Combine(Epoch, spanBits, root.ComputeHash());

    public override string ToString()
    {
        var map = "{" + string.Join(", ", Triggers.OrderBy(t => t.Key).Select(t => $"#{t.Key}: {t.Value}")) + "}";
        return IsEmpty ? map : $"{map}@{Epoch:x}";
    }

    // Wire format, FROZEN at version 2 (any future change bumps the version byte; Deserialize
    // rejects versions it does not know):
    //   stamp  := version:byte varint(epoch) spanBits:byte node
    //   node   := varint(N << 1 | isLeaf) [node node]   -- children present iff isLeaf bit is 0
    //   varint := little-endian base-128, high bit = continuation
    // Serialization of the canonical tree is deterministic: equal stamps yield equal bytes.
    private const byte FormatVersion = 2;

    public byte[] Serialize()
    {
        var bytes = new List<byte> { FormatVersion };
        WriteVarint(bytes, (ulong)Epoch);
        bytes.Add((byte)spanBits);
        root.WriteTo(bytes);
        return [.. bytes];
    }

    public static CausalityStamp Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            throw new FormatException("Causality stamp payload is truncated.");
        }
        if (bytes[0] != FormatVersion)
        {
            throw new FormatException($"Unknown causality stamp format version {bytes[0]}.");
        }

        var position = 1;
        var epoch = (long)ReadVarint(bytes, ref position);
        if (position >= bytes.Length)
        {
            throw new FormatException("Causality stamp payload is truncated.");
        }
        int bits = bytes[position++];
        if (bits > 31)
        {
            throw new FormatException($"Causality stamp span 2^{bits} exceeds the id space.");
        }

        var tree = EventTree.ReadFrom(bytes, ref position, bits);
        if (position != bytes.Length)
        {
            throw new FormatException("Causality stamp payload has trailing bytes.");
        }

        // Re-canonicalize defensively: our own serializer only emits canonical trees, but the
        // equality/serialization guarantees must hold for any parseable input.
        var stamp = FromCanonicalTree(tree.Canonicalize(), bits, epoch);
        if (!stamp.IsEmpty && stamp.Epoch == 0)
        {
            throw new FormatException("Non-empty causality stamp lacks an incarnation epoch.");
        }
        return stamp;
    }

    // Trims a canonical tree to its minimal span: an all-zero upper half is dropped (the root
    // is then Node(0, lower, Leaf 0) -- a root with N > 0 tracks every id in its span and an
    // all-zero subtree always collapses to Leaf 0, so this test is exhaustive). Equal logical
    // maps thereby get identical (root, spanBits) pairs regardless of construction order. An
    // all-zero tree claims nothing, so it canonicalizes to the epoch-agnostic Empty.
    private static CausalityStamp FromCanonicalTree(EventTree tree, int bits, long epoch)
    {
        while (bits > 0 && !tree.IsLeaf && tree.N == 0 && tree.Right!.IsLeaf && tree.Right.N == 0)
        {
            tree = tree.Left!;
            bits--;
        }
        if (tree.IsLeaf && tree.N == 0)
        {
            return Empty;
        }
        return new(tree, bits, epoch);
    }

    private static void WriteVarint(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int position)
    {
        var value = 0UL;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= bytes.Length)
            {
                throw new FormatException("Causality stamp payload is truncated.");
            }
            var b = bytes[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }
        throw new FormatException("Causality stamp varint is malformed.");
    }

    // The ITC-style event tree. Value at id = sum of N along the path; a leaf is uniform over
    // its whole span. Normal form (every tree built through MkNode): (1) an internal node's
    // children have Math.Min(Left.N, Right.N) == 0 -- the shared minimum is lifted into the
    // parent, so a subtree's minimum value IS its root N; (2) no internal node has two leaf
    // children with equal N (collapsed to a leaf). Nodes are immutable and freely shared.
    private sealed class EventTree
    {
        public static readonly EventTree Zero = new(0, null, null);

        public readonly long N;
        public readonly EventTree? Left;  // null iff leaf (always together with Right)
        public readonly EventTree? Right;

        private EventTree(long n, EventTree? left, EventTree? right)
        {
            N = n;
            Left = left;
            Right = right;
        }

        public bool IsLeaf => Left == null;

        private static EventTree Leaf(long n) => n == 0 ? Zero : new(n, null, null);

        // Normalizing constructor: assumes canonical children, returns a canonical tree.
        private static EventTree MkNode(long n, EventTree left, EventTree right)
        {
            if (left.IsLeaf && right.IsLeaf && left.N == right.N)
            {
                return Leaf(n + left.N);
            }

            var min = Math.Min(left.N, right.N);
            if (min != 0)
            {
                return new(n + min, left.Sink(min), right.Sink(min));
            }
            return new(n, left, right);
        }

        private EventTree Sink(long by) => IsLeaf ? Leaf(N - by) : new(N - by, Left, Right);

        private EventTree Lift(long by) => by == 0 ? this : IsLeaf ? Leaf(N + by) : new(N + by, Left, Right);

        public static EventTree Singleton(int id, int bits, long value)
        {
            if (bits == 0)
            {
                return Leaf(value);
            }

            var half = 1 << (bits - 1);
            return id < half
                ? MkNode(0, Singleton(id, bits - 1, value), Zero)
                : MkNode(0, Zero, Singleton(id - half, bits - 1, value));
        }

        // Widens the span from 2^fromBits to 2^toBits; the new upper ids are untracked.
        public EventTree Grow(int fromBits, int toBits)
        {
            var tree = this;
            for (var bits = fromBits; bits < toBits; bits++)
            {
                tree = MkNode(0, tree, Zero);
            }
            return tree;
        }

        public long ValueAt(int id, int bits)
        {
            var value = N;
            var tree = this;
            while (!tree.IsLeaf)
            {
                var half = 1 << --bits;
                tree = id < half ? tree.Left! : tree.Right!;
                if (id >= half)
                {
                    id -= half;
                }
                value += tree.N;
            }
            return value;
        }

        // Pointwise max of two same-span trees (the ITC event join, with the smaller base
        // lifted into the larger side's children).
        public static EventTree Join(EventTree a, EventTree b)
        {
            if (ReferenceEquals(a, b))
            {
                return a;
            }
            if (a.IsLeaf && b.IsLeaf)
            {
                return Leaf(Math.Max(a.N, b.N));
            }

            if (a.N > b.N)
            {
                (a, b) = (b, a);
            }
            var lift = b.N - a.N;
            var aLeft = a.IsLeaf ? Zero : a.Left!;
            var aRight = a.IsLeaf ? Zero : a.Right!;
            var bLeft = (b.IsLeaf ? Zero : b.Left!).Lift(lift);
            var bRight = (b.IsLeaf ? Zero : b.Right!).Lift(lift);
            return MkNode(a.N, Join(aLeft, bLeft), Join(aRight, bRight));
        }

        // Do the two same-span trees agree wherever BOTH are nonzero? accA/accB carry the path
        // sums; a uniform-zero region on either side can never conflict, so it prunes the walk.
        public static bool Consistent(EventTree a, long accA, EventTree b, long accB)
        {
            if (a.IsLeaf && accA + a.N == 0)
            {
                return true;
            }
            if (b.IsLeaf && accB + b.N == 0)
            {
                return true;
            }
            if (a.IsLeaf && b.IsLeaf)
            {
                return accA + a.N == accB + b.N;
            }
            if (ReferenceEquals(a, b) && accA == accB)
            {
                return true;
            }

            var nextA = accA + a.N;
            var nextB = accB + b.N;
            return Consistent(a.IsLeaf ? Zero : a.Left!, nextA, b.IsLeaf ? Zero : b.Left!, nextB)
                && Consistent(a.IsLeaf ? Zero : a.Right!, nextA, b.IsLeaf ? Zero : b.Right!, nextB);
        }

        // Pointwise ≤ of two same-span trees. A shared subtree compares by base alone, and a
        // uniform (leaf) left side compares against the right side's MINIMUM in O(1) -- in
        // normal form a subtree's minimum value is exactly its root N.
        public static bool Leq(EventTree a, long accA, EventTree b, long accB)
        {
            if (ReferenceEquals(a, b))
            {
                return accA <= accB;
            }
            if (a.IsLeaf)
            {
                return accA + a.N <= accB + b.N;
            }

            var nextA = accA + a.N;
            var nextB = accB + b.N;
            return Leq(a.Left!, nextA, b.IsLeaf ? Zero : b.Left!, nextB)
                && Leq(a.Right!, nextA, b.IsLeaf ? Zero : b.Right!, nextB);
        }

        public static bool StructurallyEqual(EventTree a, EventTree b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a.N != b.N || a.IsLeaf != b.IsLeaf)
            {
                return false;
            }
            return a.IsLeaf || (StructurallyEqual(a.Left!, b.Left!) && StructurallyEqual(a.Right!, b.Right!));
        }

        public int ComputeHash() =>
            IsLeaf ? HashCode.Combine(N) : HashCode.Combine(N, Left!.ComputeHash(), Right!.ComputeHash());

        // Restores the normal-form invariants of a parseable but possibly non-canonical tree.
        public EventTree Canonicalize() =>
            IsLeaf ? Leaf(N) : MkNode(N, Left!.Canonicalize(), Right!.Canonicalize());

        public void CollectTracked(int bits, int baseId, long acc, Dictionary<int, long> into)
        {
            var value = acc + N;
            if (IsLeaf)
            {
                if (value == 0)
                {
                    return;
                }
                // Long arithmetic: a leaf can span the full 31-bit id space, where 1 << bits
                // overflows int.
                for (var id = (long)baseId; id < baseId + (1L << bits); id++)
                {
                    into[(int)id] = value - 1;
                }
                return;
            }

            Left!.CollectTracked(bits - 1, baseId, value, into);
            Right!.CollectTracked(bits - 1, baseId + (1 << (bits - 1)), value, into);
        }

        public void WriteTo(List<byte> bytes)
        {
            WriteVarint(bytes, ((ulong)N << 1) | (IsLeaf ? 1UL : 0UL));
            if (!IsLeaf)
            {
                Left!.WriteTo(bytes);
                Right!.WriteTo(bytes);
            }
        }

        // bits is the remaining span: a node spanning a single id (bits == 0) must be a leaf,
        // which both validates the payload and bounds the recursion at the 31-bit id space.
        public static EventTree ReadFrom(ReadOnlySpan<byte> bytes, ref int position, int bits)
        {
            var header = ReadVarint(bytes, ref position);
            var n = (long)(header >> 1);
            if ((header & 1) != 0)
            {
                return Leaf(n);
            }
            if (bits == 0)
            {
                throw new FormatException("Causality stamp tree is deeper than its span.");
            }

            var left = ReadFrom(bytes, ref position, bits - 1);
            var right = ReadFrom(bytes, ref position, bits - 1);
            return new(n, left, right);
        }
    }
}
