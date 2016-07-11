using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    public class ExceptionsTests
    {
        public ExceptionsTests()
        {
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
            SerializationManager.Initialize(false, null, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_DotNet()
        {
            ActivationAddress activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());
            SiloAddress primaryDirectoryForGrain = SiloAddress.NewLocalAddress(6789);
           
            Catalog.DuplicateActivationException original = new Catalog.DuplicateActivationException(activationAddress, primaryDirectoryForGrain);
            Catalog.DuplicateActivationException output = TestingUtils.RoundTripDotNetSerializer(original);

            Assert.AreEqual(original.Message, output.Message);
            Assert.AreEqual(original.ActivationToUse, output.ActivationToUse);
            Assert.AreEqual(original.PrimaryDirectoryForGrain, output.PrimaryDirectoryForGrain);
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_Orleans()
        {
            ActivationAddress activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());
            SiloAddress primaryDirectoryForGrain = SiloAddress.NewLocalAddress(6789);

            Catalog.DuplicateActivationException original = new Catalog.DuplicateActivationException(activationAddress, primaryDirectoryForGrain);
            Catalog.DuplicateActivationException output = SerializationManager.RoundTripSerializationForTesting(original);

            Assert.AreEqual(original.Message, output.Message);
            Assert.AreEqual(original.ActivationToUse, output.ActivationToUse);
            Assert.AreEqual(original.PrimaryDirectoryForGrain, output.PrimaryDirectoryForGrain);
        }
    }
}
