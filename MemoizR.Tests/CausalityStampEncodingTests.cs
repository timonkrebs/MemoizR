namespace MemoizR.Tests;

// Phase 2 of the Causality Trigger Clock (issue #39): the ITC-inspired event-tree encoding
// behind CausalityStamp and its wire format. The phase-1 suite (CausalityTriggerClockTests)
// pins the semantics; this suite pins the representation: canonical-form uniqueness,
// equivalence with a naive dictionary model under randomized operation sequences, and the
// serialization contract.
public class CausalityStampEncodingTests
{
    // A fixed incarnation epoch (small, so it stays a single golden byte on the wire).
    private const long Epoch = 9;

    // The naive phase-1 semantics, kept as the executable specification the tree encoding is
    // checked against.
    private sealed class ReferenceStamp
    {
        public Dictionary<int, long> Triggers { get; } = new();

        public static ReferenceStamp For(int id, long trigger) => new() { Triggers = { [id] = trigger } };

        public ReferenceStamp Join(ReferenceStamp other)
        {
            var joined = new ReferenceStamp();
            foreach (var (id, trigger) in Triggers)
            {
                joined.Triggers[id] = trigger;
            }
            foreach (var (id, trigger) in other.Triggers)
            {
                joined.Triggers[id] = joined.Triggers.TryGetValue(id, out var existing) && existing >= trigger ? existing : trigger;
            }
            return joined;
        }

        public bool IsConsistentWith(ReferenceStamp other)
        {
            foreach (var (id, trigger) in Triggers)
            {
                if (other.Triggers.TryGetValue(id, out var otherTrigger) && otherTrigger != trigger)
                {
                    return false;
                }
            }
            return true;
        }

        public bool IsDominatedBy(ReferenceStamp other) =>
            Triggers.All(t => other.Triggers.TryGetValue(t.Key, out var v) && v >= t.Value);

        public bool SameMapAs(ReferenceStamp other) =>
            Triggers.Count == other.Triggers.Count
            && Triggers.All(t => other.Triggers.TryGetValue(t.Key, out var v) && v == t.Value);
    }

    private static void AssertMatchesReference(CausalityStamp stamp, ReferenceStamp reference)
    {
        var triggers = stamp.Triggers;
        Assert.Equal(reference.Triggers.Count, triggers.Count);
        foreach (var (id, trigger) in reference.Triggers)
        {
            Assert.True(triggers.TryGetValue(id, out var actual));
            Assert.Equal(trigger, actual);

            Assert.True(stamp.TryGetTrigger(id, out var queried));
            Assert.Equal(trigger, queried);
        }
    }

    [Fact]
    public void RandomizedJoins_MatchTheDictionaryModel()
    {
        // Small id/trigger ranges force heavy overlap, so joins constantly merge, lift and
        // collapse tree regions. Fixed seeds keep the test deterministic.
        foreach (var seed in new[] { 1, 2, 3, 4, 5 })
        {
            var rng = new Random(seed);
            var stamps = new List<CausalityStamp> { CausalityStamp.Empty };
            var references = new List<ReferenceStamp> { new() };

            for (var step = 0; step < 200; step++)
            {
                if (rng.Next(3) == 0 || stamps.Count < 2)
                {
                    var id = rng.Next(64);
                    var trigger = rng.Next(4);
                    stamps.Add(CausalityStamp.ForSignal(id, trigger, Epoch));
                    references.Add(ReferenceStamp.For(id, trigger));
                }
                else
                {
                    var a = rng.Next(stamps.Count);
                    var b = rng.Next(stamps.Count);
                    stamps.Add(stamps[a].Join(stamps[b]));
                    references.Add(references[a].Join(references[b]));

                    // The join is the least upper bound: it dominates both operands.
                    Assert.True(stamps[a].IsDominatedBy(stamps[^1]));
                    Assert.True(stamps[b].IsDominatedBy(stamps[^1]));
                }

                AssertMatchesReference(stamps[^1], references[^1]);
            }

            // Pairwise relations on a sample, against the model: consistency, equality (which
            // must coincide with map equality), hash agreement, and untracked-id queries.
            for (var i = 0; i < 60; i++)
            {
                var a = rng.Next(stamps.Count);
                var b = rng.Next(stamps.Count);
                Assert.Equal(references[a].IsConsistentWith(references[b]), stamps[a].IsConsistentWith(stamps[b]));
                Assert.Equal(references[b].IsConsistentWith(references[a]), stamps[b].IsConsistentWith(stamps[a]));
                Assert.Equal(references[a].IsDominatedBy(references[b]), stamps[a].IsDominatedBy(stamps[b]));
                Assert.Equal(references[b].IsDominatedBy(references[a]), stamps[b].IsDominatedBy(stamps[a]));

                var mapsEqual = references[a].SameMapAs(references[b]);
                Assert.Equal(mapsEqual, stamps[a].Equals(stamps[b]));
                if (mapsEqual)
                {
                    Assert.Equal(stamps[a].GetHashCode(), stamps[b].GetHashCode());
                }

                var untracked = 64 + rng.Next(64);
                Assert.False(stamps[a].TryGetTrigger(untracked, out _));
            }
        }
    }

    [Fact]
    public void CanonicalForm_MakesSerializationJoinOrderIndependent()
    {
        // The same logical map assembled in different join orders must produce byte-identical
        // payloads -- the canonical (normalized + trimmed) tree is unique per map.
        var entries = new (int Id, long Trigger)[] { (1, 4), (2, 7), (3, 2), (10, 0), (11, 0), (40, 9) };

        var ascending = CausalityStamp.JoinAll(entries.Select(e => CausalityStamp.ForSignal(e.Id, e.Trigger, Epoch)));
        var descending = CausalityStamp.JoinAll(entries.Reverse().Select(e => CausalityStamp.ForSignal(e.Id, e.Trigger, Epoch)));
        var interleaved = entries.Skip(3).Aggregate(
            CausalityStamp.JoinAll(entries.Take(3).Select(e => CausalityStamp.ForSignal(e.Id, e.Trigger, Epoch))),
            (acc, e) => CausalityStamp.ForSignal(e.Id, e.Trigger, Epoch).Join(acc));

        Assert.Equal(ascending, descending);
        Assert.Equal(ascending, interleaved);
        Assert.Equal(ascending.Serialize(), descending.Serialize());
        Assert.Equal(ascending.Serialize(), interleaved.Serialize());
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsRandomStamps()
    {
        var rng = new Random(7);
        var stamp = CausalityStamp.Empty;
        for (var step = 0; step < 150; step++)
        {
            stamp = stamp.Join(CausalityStamp.ForSignal(rng.Next(512), rng.Next(6), Epoch));

            var roundTripped = CausalityStamp.Deserialize(stamp.Serialize());
            Assert.Equal(stamp, roundTripped);
            Assert.Equal(stamp.GetHashCode(), roundTripped.GetHashCode());
            Assert.Equal(Epoch, roundTripped.Epoch);
            Assert.Equal(stamp.Serialize(), roundTripped.Serialize());
        }
    }

    [Fact]
    public void Serialize_GoldenBytes()
    {
        // Pins the FROZEN v2 wire format (version byte, varint epoch, spanBits byte, preorder
        // varint(N << 1 | leaf) nodes): any change must arrive as a deliberate version bump.
        // The empty stamp is epoch-agnostic (epoch 0).
        Assert.Equal(new byte[] { 2, 0, 0, 1 }, CausalityStamp.Empty.Serialize());

        // ForSignal(1, 4) @ epoch 9: span 2, Node(0, Leaf 0, Leaf 5) -> headers 0, 1, 11.
        Assert.Equal(new byte[] { 2, 9, 1, 0, 1, 11 }, CausalityStamp.ForSignal(1, 4, Epoch).Serialize());

        // A trigger needing the varint continuation byte: 200 -> value 201 -> header 403.
        Assert.Equal(new byte[] { 2, 9, 0, 147, 3 }, CausalityStamp.ForSignal(0, 200, Epoch).Serialize());
    }

    [Fact]
    public void Deserialize_CanonicalizesForeignInput()
    {
        // Node(0, Leaf 3, Leaf 3) over span 2 is parseable but non-canonical; it must read back
        // equal -- and re-serialize byte-identical -- to the canonical Leaf(3) built via joins.
        var foreign = CausalityStamp.Deserialize(new byte[] { 2, 9, 1, 0, 7, 7 });
        var canonical = CausalityStamp.ForSignal(0, 2, Epoch).Join(CausalityStamp.ForSignal(1, 2, Epoch));

        Assert.Equal(canonical, foreign);
        Assert.Equal(canonical.Serialize(), foreign.Serialize());
    }

    [Fact]
    public void Deserialize_RejectsMalformedPayloads()
    {
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(Array.Empty<byte>()));

        // Truncated after the version byte / after the epoch.
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2 }));
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 9 }));

        // Unknown versions (v1 was never frozen and is deliberately not readable).
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 1, 0, 0, 1 }));
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 3, 0, 0, 1 }));

        // Span beyond the 31-bit id space.
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 9, 40, 1 }));

        // Internal node with a missing child (truncated tree).
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 9, 1, 0, 1 }));

        // Internal node where the span only covers a single id.
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 9, 0, 0, 1, 1 }));

        // Trailing bytes after a complete tree.
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 9, 0, 1, 9 }));

        // Unterminated varint continuation chains -- in the epoch and in a tree header.
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 0x80, 0x80 }));
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 9, 0, 0x80, 0x80 }));

        // A non-empty stamp must carry an incarnation epoch (Leaf 1 over id 0, epoch 0).
        Assert.Throws<FormatException>(() => CausalityStamp.Deserialize(new byte[] { 2, 0, 0, 3 }));
    }

    [Fact]
    public void ContiguousFreshSignals_CollapseToAHandfulOfBytes()
    {
        // The space-efficiency claim of issue #39 on the common shape it targets: a batch of
        // signals created together (contiguous ids) and not yet set (all trigger 0). The 64
        // entries occupy one uniform tree region -- ids 64..127 are exactly the upper half of
        // [0, 128) -- so the payload stays a handful of bytes where a naive (id, trigger) list
        // needs hundreds.
        var batch = CausalityStamp.JoinAll(Enumerable.Range(64, 64).Select(id => CausalityStamp.ForSignal(id, 0, Epoch)));

        Assert.Equal(64, batch.Triggers.Count);
        Assert.True(batch.Serialize().Length <= 8,
            $"expected a collapsed payload, got {batch.Serialize().Length} bytes");

        // Misaligned ranges split into log-many regions, not per-id entries: still far smaller
        // than the naive list.
        var misaligned = CausalityStamp.JoinAll(Enumerable.Range(100, 64).Select(id => CausalityStamp.ForSignal(id, 0, Epoch)));
        Assert.Equal(64, misaligned.Triggers.Count);
        Assert.True(misaligned.Serialize().Length <= 40,
            $"expected a run-compressed payload, got {misaligned.Serialize().Length} bytes");
    }

    [Fact]
    public void LargeIds_NearTheEdgeOfTheIdSpace_Work()
    {
        var huge = CausalityStamp.ForSignal(int.MaxValue, 7, Epoch).Join(CausalityStamp.ForSignal(3, 1, Epoch));

        Assert.True(huge.TryGetTrigger(int.MaxValue, out var trigger));
        Assert.Equal(7, trigger);
        Assert.True(huge.TryGetTrigger(3, out var small));
        Assert.Equal(1, small);
        Assert.Equal(2, huge.Triggers.Count);

        Assert.Equal(huge, CausalityStamp.Deserialize(huge.Serialize()));
    }
}
