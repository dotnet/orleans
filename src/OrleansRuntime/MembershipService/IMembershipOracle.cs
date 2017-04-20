namespace Orleans.Runtime
{
    /// <summary>
    /// Authoritative source for cluster membership.
    /// </summary>
    public interface IMembershipOracle : ISiloStatusOracle, IHealthCheckParticipant
    {
    }
}
