using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IReminderTestGrain : IGrainWithIntegerKey
    {
        Task<bool> IsReminderExists(string reminderName);
    }
}