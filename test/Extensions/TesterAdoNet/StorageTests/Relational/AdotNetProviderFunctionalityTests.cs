using Orleans.Storage;
using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Xunit;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Tests for various helper classes.
    /// </summary>
    public class AdotNetProviderFunctionalityTests
    {
        [TestCategory("Functional"), TestCategory("Persistence")]
        [Fact]
        public void AdoNetStorageProviderGrainTypeHashing()
        {
            //This way of using the hasher is like ADO.NET Storage provider would use it. This tests
            //the hasher is thread safe.
            var adonetDefaultHasher = new StorageHasherPicker(new[] { new OrleansDefaultHasher() });
            const int TestGrainHash = -201809205;
            var grainType = "Grains.PersonGrain";
            Parallel.For(0, 1000000, i =>
            {
                //These parameters can be null in this test.
                int grainTypeHash = adonetDefaultHasher.PickHasher<object>(null, null, default, null, null).Hash(Encoding.UTF8.GetBytes(grainType));
                Assert.Equal(TestGrainHash, grainTypeHash);
            });
        }
    }
}
