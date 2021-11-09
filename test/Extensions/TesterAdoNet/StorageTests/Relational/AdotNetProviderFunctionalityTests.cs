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
            var adonetDefaultHasher = new StorageHasherPicker(new[] { new OrleansDefaultHasher() }); ;
            const int TestGrainHash = 1491221312;
            var grainType = "Grains.PersonGrain";
            Parallel.For(0, 1000000, i =>
            {
                //These parameters can be null in this test.
                int grainTypeHash = adonetDefaultHasher.PickHasher<object>(null, null, null, null, null, null).Hash(Encoding.UTF8.GetBytes(grainType));
                Assert.Equal(TestGrainHash, grainTypeHash);
            });
        }

        [TestCategory("Functional"), TestCategory("Persistence")]
        [Fact]
        public void LongGrainIdToN1KeyAreSame()
        {
            const long LongGrainId = 1001;
            var longGrainIdAsN1 = new AdoGrainKey(LongGrainId, null);

            Assert.Equal(longGrainIdAsN1.N1Key, LongGrainId);
        }


        [TestCategory("Functional"), TestCategory("Persistence")]
        [Fact]
        public void LongGrainIdToStringAreSame()
        {
            const long LongGrainId = 1001;
            var longGrainIdAsString = new AdoGrainKey(LongGrainId, null).ToString();

            Assert.Equal(longGrainIdAsString, LongGrainId.ToString(CultureInfo.InvariantCulture));
        }


        [TestCategory("Functional"), TestCategory("Persistence")]
        [Fact]
        public void LongGrainIdWithExtensionAreSame()
        {
            const long LongGrainId = 1001;
            const string ExtensionKey = "ExtensionKey";
            var longGrainIdWitExtensionAsString = new AdoGrainKey(LongGrainId, ExtensionKey).ToString();

            //AdoGrainKey helper class splits the grain key and extension key using character '#'.
            //The key and its extension are the two distinct elements.
            var grainKeys = longGrainIdWitExtensionAsString.Split(new[] { "#" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, grainKeys.Length);

            Assert.Equal(grainKeys[0], LongGrainId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(grainKeys[1], ExtensionKey);
        }


        [TestCategory("Functional"), TestCategory("Persistence")]
        [Fact]
        public void GuidGrainIdWithExtensionAreSame()
        {
            Guid guidId = Guid.Parse("751D8030-9C84-4A91-816E-E95F64CE7588");
            var guidIdAsString = new AdoGrainKey(guidId, null).ToString();

            Assert.Equal(guidIdAsString, guidId.ToString());
        }
    }
}
