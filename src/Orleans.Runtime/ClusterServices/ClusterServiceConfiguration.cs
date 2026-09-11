namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Fixed assignment inputs for the membership-derived provider. Participating hosts agree on these values.
/// </summary>
internal sealed class ClusterServiceConfiguration
{
    public ClusterServiceConfiguration(
        string serviceId,
        int partitionsPerSilo,
        string assignmentStrategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionsPerSilo, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignmentStrategy);

        ServiceId = serviceId;
        PartitionsPerSilo = partitionsPerSilo;
        AssignmentStrategy = assignmentStrategy;
    }

    public string ServiceId { get; }

    public int PartitionsPerSilo { get; }

    public string AssignmentStrategy { get; }
}
