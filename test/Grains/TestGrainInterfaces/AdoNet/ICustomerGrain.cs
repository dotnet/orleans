namespace Orleans.SqlUtils.StorageProvider.GrainInterfaces
{
    public interface ICustomerGrain : IGrainWithIntegerKey
    {
        Task<string> IntroduceSelf();
         
        Task Set(int customerId, string firstName, string lastName);

        Task AddDevice(IDeviceGrain device);

        Task SetRandomState();
    }
}
