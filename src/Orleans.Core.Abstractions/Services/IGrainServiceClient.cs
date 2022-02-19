namespace Orleans.Services
{
    /// <summary>
    /// Base interface for grain service clients.
    /// </summary>
    /// <typeparam name="TGrainService">The grain service interface type.</typeparam>
    public interface IGrainServiceClient<TGrainService>
        where TGrainService : IGrainService
    {
    }
}