namespace Orleans.SqlUtils.StorageProvider
{
    public interface IGrainStateMapFactory
    {
        GrainStateMap CreateGrainStateMap();
    }
}