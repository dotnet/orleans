using System.Threading.Tasks;
using Xunit;

namespace Grains.Tests.Isolated
{
    public class BasicGrainTests
    {
        [Fact]
        public async Task Gets_And_Sets_Value()
        {
            // create a new grain
            // we do not need to mock the grain here because we do not use orleans services in this test
            var grain = new BasicGrain();

            // assert the default value is zero
            Assert.Equal(0, await grain.GetValueAsync());

            // set a new value
            await grain.SetValueAsync(123);

            // assert the new value is as set
            Assert.Equal(123, await grain.GetValueAsync());
        }
    }
}