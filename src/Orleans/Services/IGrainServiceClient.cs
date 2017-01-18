namespace Orleans.Services
{
    public interface IGrainServiceClient<TGrainService> where TGrainService : IGrainService
    {
    }
}