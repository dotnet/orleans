namespace UnitTests.GrainInterfaces
{
    public interface ISiloRoleBasedPlacementGrain : IGrainWithStringKey
    {
        Task<bool> Ping();
    }
}
