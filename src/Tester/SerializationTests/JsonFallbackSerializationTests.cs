namespace UnitTests.Serialization
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Orleans.Serialization;

    /// <summary>
    /// Tests for the serialization system.
    /// </summary>
    [TestClass]
    public class JsonFallbackSerializationTests : SerializationTestsJsonTypes
    {
        /// <summary>
        /// Initializes the system for testing.
        /// </summary>
        [TestInitialize]
        public new void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting(useJsonFallbackSerializer: true);
        }
    }
}
