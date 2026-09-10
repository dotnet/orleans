extern alias ClassicRemindersAdoNet;

using RelationalOrleansQueries = ClassicRemindersAdoNet::Orleans.Reminders.AdoNet.Storage.RelationalOrleansQueries;
using Xunit;

namespace UnitTests.RemindersTest;

public class ReminderRangeQuerySelectionTests
{
    [Fact]
    public void SignedQuerySelectionPreservesUnsignedRingSemanticsAtBoundaries()
    {
        uint[] boundaries =
        [
            0,
            1,
            int.MaxValue,
            (uint)int.MaxValue + 1,
            uint.MaxValue - 1,
            uint.MaxValue
        ];

        foreach (var begin in boundaries)
        {
            foreach (var end in boundaries)
            {
                foreach (var hash in boundaries)
                {
                    var expected = begin < end
                        ? hash > begin && hash <= end
                        : hash > begin || hash <= end;
                    var signedHash = unchecked((int)hash);
                    var signedBegin = unchecked((int)begin);
                    var signedEnd = unchecked((int)end);
                    var actual = RelationalOrleansQueries.IsReminderRangeNonWrappingInSignedOrder(begin, end)
                        ? signedHash > signedBegin && signedHash <= signedEnd
                        : signedHash > signedBegin || signedHash <= signedEnd;

                    Assert.Equal(expected, actual);
                }
            }
        }
    }
}
