using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStreamingImmutabilityTestGrain : IGrainWithGuidKey
    {
        Task SubscribeToStream(Guid guid, string providerName);
        Task UnsubscribeFromStream();
        Task SendTestObject(string providerName);
        Task SetTestObjectStringProperty(string value);
        Task<string> GetTestObjectStringProperty();
        Task<string> GetSiloIdentifier();

    }
}