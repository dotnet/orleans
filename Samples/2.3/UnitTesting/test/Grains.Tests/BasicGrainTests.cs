using System.Threading.Tasks;
using Xunit;

namespace Grains.Tests
{
    public class BasicGrainTests
    {
        [Fact]
        public async Task Gets_And_Sets_Value()
        {
            // Create a new grain.
            // As this test does not interact with orleans services, we do not need to mock the grain.
            var grain = new BasicGrain();

            // Assert the default value is zero.
            Assert.Equal(0, await grain.GetValueAsync());

            // Act by setting a new value.
            await grain.SetValueAsync(123);

            // Assert the new value is as set.
            Assert.Equal(123, await grain.GetValueAsync());
        }
    }
}