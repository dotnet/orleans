using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Runtime.Configuration;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Tests for the serialization system.
    /// </summary>
    [TestClass]
    public class FallbackBuiltInSerializationTests : BuiltInSerializerTests
    {
        /// <summary>
        /// Initializes the system for testing.
        /// </summary>
        [TestInitialize]
        public new void InitializeForTesting()
        {
            TraceLogger.Initialize(new NodeConfiguration());
            SerializationManager.Initialize(false, null, true);
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

        public override void Serialize_Predicate()
        {
            // there's no ability to serialize expressions with Json.Net serializer yet.
        }

        public override void Serialize_Func()
        {
            // there's no ability to serialize expressions with Json.Net serializer yet.
        }
    }
}
