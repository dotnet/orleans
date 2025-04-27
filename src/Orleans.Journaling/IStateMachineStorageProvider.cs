namespace Orleans.Journaling;

public interface IStateMachineStorageProvider
{
    IStateMachineStorage Create(IGrainContext grainContext);
}
