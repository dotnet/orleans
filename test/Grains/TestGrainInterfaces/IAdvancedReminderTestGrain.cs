#nullable enable

namespace UnitTests.GrainInterfaces;

public interface IAdvancedReminderTestGrain : IGrainWithIntegerKey
{
    Task Register(string name, TimeSpan dueTime, TimeSpan period);

    Task Unregister(string name);

    Task<bool> Exists(string name);

    Task<int> GetTickCount();

    Task<string> UpsertRaw(string name, string eTag);

    Task<string?> ReadRawETag(string name);

    Task<int> ReadRawGrainCount();

    Task<int> ReadRawContainingRangeCount();

    Task<bool> RemoveRaw(string name, string eTag);

    Task ClearRawTable();
}
