using System;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    internal interface IMembershipService : ISystemTarget
    {
        /// <summary>
        /// Receive notifications about a change in the membership table
        /// </summary>
        /// <param name="snapshot">Snapshot of the membership table</param>
        /// <returns></returns>
        Task MembershipChangeNotification(MembershipTableSnapshot snapshot);

        /// <summary>
        /// Ping request from another silo that probes the liveness of the recipient silo.
        /// </summary>
        /// <param name="pingNumber">A unique sequence number for ping message, to facilitate testing and debugging.</param>
        Task Ping(int pingNumber);

        Task<IndirectProbeResponse> ProbeIndirectly(SiloAddress target, TimeSpan probeTimeout, int probeNumber);
    }

    /// <summary>
    /// Represents the result of probing a node via an intermediary node.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public readonly struct IndirectProbeResponse
    {
        /// <summary>
        /// The health score of the intermediary node.
        /// </summary>
        [Id(0)]
        public int IntermediaryHealthScore { get; init; }

        /// <summary>
        /// <see langword="true"/> if the probe succeeded; otherwise, <see langword="false"/>.
        /// </summary>
        [Id(1)]
        public bool Succeeded { get; init; }

        /// <summary>
        /// The duration of the probe attempt.
        /// </summary>
        [Id(2)]
        public TimeSpan ProbeResponseTime { get; init; }

        /// <summary>
        /// The failure message if the probe did not succeed.
        /// </summary>
        [Id(3)]
        public string FailureMessage { get; init; }

        /// <inheritdoc />
        public override string ToString() => $"IndirectProbeResponse {{ Succeeded: {Succeeded}, IntermediaryHealthScore: {IntermediaryHealthScore}, ProbeResponseTime: {ProbeResponseTime}, FailureMessage: {FailureMessage} }}";
    }
}
