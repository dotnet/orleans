using System.Collections.ObjectModel;
using System.Fabric.Description;

namespace TestServiceFabric
{
    public class EndpointsCollection : KeyedCollection<string, EndpointResourceDescription>
    {
        protected override string GetKeyForItem(EndpointResourceDescription item) => item?.Name;
    }
}