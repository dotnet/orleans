using Orleans;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    public interface IReminderTestGrain : IGrainWithIntegerKey
    {
        Task<bool> IsReminderExists(string reminderName);
    }
}