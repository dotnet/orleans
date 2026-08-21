namespace Orleans.Runtime.Hosting.Clustering;

/// <summary>
/// Represents a member reported by an external hosting environment.
/// </summary>
public class ExternalClusterMember
{
    /// <summary>
    /// Gets the stable member name which corresponds to the Orleans silo name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the provider-specific member description used in diagnostics.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets a value indicating whether this member represents the local silo.
    /// </summary>
    /// <remarks>
    /// Providers set this value for the local silo so that deletion events cannot cause it to declare itself dead.
    /// </remarks>
    public bool IsCurrentSilo { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalClusterMember"/> class.
    /// </summary>
    /// <param name="name">The stable member name which corresponds to the Orleans silo name.</param>
    /// <param name="description">The provider-specific member description.</param>
    public ExternalClusterMember(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Description;
    }
}

/// <summary>
/// Represents notification that an external cluster member was deleted.
/// </summary>
public sealed class ClusterMemberDeleted : ClusterEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterMemberDeleted"/> class.
    /// </summary>
    /// <param name="member">The deleted external member.</param>
    public ClusterMemberDeleted(ExternalClusterMember member)
        : base(member)
    {
    }
}

/// <summary>
/// Represents a change reported by an external cluster provider.
/// </summary>
public abstract class ClusterEvent
{
    /// <summary>
    /// Gets the external member associated with this event.
    /// </summary>
    public ExternalClusterMember Member { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterEvent"/> class.
    /// </summary>
    /// <param name="member">The external member associated with this event.</param>
    protected ClusterEvent(ExternalClusterMember member)
    {
        Member = member;
    }
}
