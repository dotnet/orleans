namespace Microsoft.Orleans.Docker.Utilities
{
    internal enum ErrorCode
    {
        Runtime = 100000,
        DockerBase = Runtime + 4400,
        Docker_GatewayProvider_ExceptionNotifyingSubscribers = DockerBase + 1,
        Docker_GatewayProvider_ExceptionRefreshingGateways = DockerBase + 2,
        Docker_MembershipOracle_ExceptionNotifyingSubscribers = DockerBase + 3
    }
}
