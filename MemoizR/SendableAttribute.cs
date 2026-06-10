namespace MemoizR;

/// <summary>
/// Asserts that a type is safe to share across concurrently running async flows -- either deeply
/// immutable or internally synchronized -- even though <see cref="SendableChecker"/> cannot
/// verify it structurally. The analog of Swift's <c>@unchecked Sendable</c>: the attribute is
/// trusted without further checks, so applying it to a type that is in fact unsynchronized
/// mutable state reintroduces the data races strict mode exists to prevent.
/// </summary>
/// <remarks>
/// Deliberately not inherited: a derived type can add mutable state, so each type must make the
/// promise for itself.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class SendableAttribute : Attribute
{
}
