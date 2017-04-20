namespace UnitTests.GrainInterfaces
{
    using System.Threading.Tasks;

    using Orleans;
    using Orleans.Runtime;

    internal interface IDefaultPlacementGrain : IGrainWithIntegerKey
    {
        Task<PlacementStrategy> GetDefaultPlacement();
    }
}
