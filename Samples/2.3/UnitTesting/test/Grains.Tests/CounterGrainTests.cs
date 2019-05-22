using System.Threading.Tasks;
using Xunit;

namespace Grains.Tests
{
    public class IsolatedCounterGrainTests
    {
        [Fact]
        public async Task CounterGrain_Increments_Value_From_Zero()
        {
            // arrange
            var grain = new CounterGrain();

            // act
            await grain.IncrementAsync();
            var value = await grain.GetValueAsync();

            // assert
            Assert.Equal(1, value);
        }
    }
}