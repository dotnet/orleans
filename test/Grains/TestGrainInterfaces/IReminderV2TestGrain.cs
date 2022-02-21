using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IReminderV2TestGrain : IGrainWithIntegerKey
    {
        Task<bool> IsReminderExists(string reminderName);
        Task AddReminder(string reminderName);
        Task RemoveReminder(string reminderName);
    }
}