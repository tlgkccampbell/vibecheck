namespace Vibecheck;

/// <summary>
/// Names the leaf of the architecture map a type belongs to, overriding the map's
/// patterns. The checker recognizes this attribute by its full name, so a repository
/// declares it itself. See docs/vibecheck-spec.md section 2.3 in the archviz repository.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate)]
public sealed class VibecheckAttribute(string leaf) : Attribute
{
    public string Leaf { get; } = leaf;
}
