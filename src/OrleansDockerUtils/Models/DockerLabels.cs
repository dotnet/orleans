namespace Microsoft.Orleans.Docker.Models
{
    internal static class DockerLabels
    {
        public const string IS_DOCKER_SILO = "com.microsoft.orleans.silo";
        public const string DEPLOYMENT_ID = "com.microsoft.orleans.deploymentid";
        public const string SILO_PORT = "com.microsoft.orleans.siloport";
        public const string GATEWAY_PORT = "com.microsoft.orleans.gatewayport";
        public const string GENERATION = "com.microsoft.orleans.generation";
    }
}
