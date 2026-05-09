namespace Orleans.Journaling;

public interface IJournalStorageProvider
{
    IJournalStorage Create(IGrainContext grainContext);
}
