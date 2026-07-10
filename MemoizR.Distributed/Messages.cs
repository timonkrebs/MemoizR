namespace MemoizR.Distributed;

// The wire protocol is three messages: a host ADVERTISES that an exported node published
// something new (stale), a consumer PULLS, the host answers with a VALUE payload. Values move
// only on pull, so MemoizR's laziness survives the network. Both messages carry the same
// ordering header:
//
//  - Epoch: the host context's incarnation (never 0 on the wire). Epochs are random
//    identifiers, NOT ordered -- a mismatch means "a different incarnation", never "newer".
//    Carried beside the stamp because an honestly-empty stamp is epoch-agnostic, and ordering
//    must survive empty publications.
//  - Sequence: the node's publication counter, strictly increasing per epoch. This is the
//    total order over one node's publications -- including the shapes causality stamps
//    deliberately cannot order (a dependency set oscillating through empty re-publishes an
//    earlier stamp on a NEWER value).
//  - Stamp: the publication's causality stamp in the frozen v2 wire format. Not used for
//    ordering (the sequence does that); it is the CONSISTENCY evidence the glitch barrier
//    checks across nodes.

/// <summary>
/// A host's advertisement that the exported node published: "I have (epoch, sequence); pull if
/// you don't." Carries no value -- the consumer decides whether to move it.
/// </summary>
public sealed record StaleNotification(int NodeId, long Epoch, long Sequence, byte[] Stamp);

/// <summary>
/// A pull answer: one publication's value, ordering header, causality stamp and verifiability,
/// all describing the SAME atomic publication of the exported node.
/// <see cref="Unverifiable"/> means the host cannot vouch for which signal versions the value
/// reflects (a mixed or faulted evaluation) -- a consumer must stop trusting held evidence but
/// must not render this as a verified snapshot.
/// </summary>
public sealed record ValuePayload<T>(int NodeId, long Epoch, long Sequence, T Value, byte[] Stamp, bool Unverifiable);
