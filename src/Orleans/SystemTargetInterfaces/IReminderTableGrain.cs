using Orleans.Concurrency;

namespace Orleans
{
    /// <summary>
    /// Reminder table interface for grain based implementation.
    /// </summary>
    [Unordered]
    internal interface IReminderTableGrain : IGrainWithIntegerKey, IReminderTable
    {
        
    }
}