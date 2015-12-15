namespace Orleans.Runtime
{
    // The local interface of Membership Oracles.
    internal interface IMembershipOracle : ISiloStatusOracle, IHealthCheckParticipant
    {
    }
}
