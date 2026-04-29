namespace Orleans.Journaling;

public interface ILogStorageProvider
{
    ILogStorage Create(IGrainContext grainContext);
}
