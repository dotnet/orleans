using System;
using System.Fabric;
using NSubstitute;

namespace TestServiceFabric
{
    public class MockServiceContext : ServiceContext
    {
        public MockServiceContext(NodeContext nodeContext, ICodePackageActivationContext codePackageActivationContext,
            string serviceTypeName, Uri serviceName, byte[] initializationData, Guid partitionId,
            long replicaOrInstanceId)
            : base(
                nodeContext,
                codePackageActivationContext,
                serviceTypeName,
                serviceName,
                initializationData,
                partitionId,
                replicaOrInstanceId)
        {
        }
    }
}