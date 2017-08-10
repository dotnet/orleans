using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using System.Xml;
using Orleans.EventSourcing.StateStorage;

namespace Tester.EventSourcingTests
{
    /// <summary>
    /// Summary description for SerializationTests
    /// </summary>
    [Collection(EventSourceEnvironmentFixture.EventSource)]
    public class SerializationTestsJsonTypes
    {
        [Serializable]
        public class SimplePOCO
        {
            public int A { get; set; }
            public int B { get; set; }
        }

        public class AdvancedPOCO
        {
            public int A { get; set; }
            public int B { get; set; }

            [CopierMethod]
            public static object DeepCopier(object original, ICopyContext context)
            {
                AdvancedPOCO instance = (AdvancedPOCO)original;

                int a = (int)SerializationManager.DeepCopyInner(instance.A, context);
                int b = (int)SerializationManager.DeepCopyInner(instance.B, context);

                return new AdvancedPOCO { A = a, B = b };
            }

            [SerializerMethod]
            internal static void Serialize(object input, ISerializationContext context, Type expected)
            {
                AdvancedPOCO instance = (AdvancedPOCO)input;

                SerializationManager.SerializeInner(instance.A, context, typeof(int));
                SerializationManager.SerializeInner(instance.B, context, typeof(int));
            }

            [DeserializerMethod]
            internal static object Deserialize(Type expected, IDeserializationContext context)
            {
                int a = (int)SerializationManager.DeserializeInner<int>(context);
                int b = (int)SerializationManager.DeserializeInner<int>(context);

                return new AdvancedPOCO { A = a, B = b };
            }
        }

        public class ReportingPOCO
        {
            public int A { get; set; }
            public int B { get; set; }
            public int CopyCount { get; set; }
            public int SerializeCount { get; set; }
            public int DeserializeCount { get; set; }

            [CopierMethod]
            public static object DeepCopier(object original, ICopyContext context)
            {
                ReportingPOCO instance = (ReportingPOCO)original;

                int a = (int)SerializationManager.DeepCopyInner(instance.A, context);
                int b = (int)SerializationManager.DeepCopyInner(instance.B, context);
                int copyCount = (int)SerializationManager.DeepCopyInner(instance.CopyCount, context);
                int serializeCount = (int)SerializationManager.DeepCopyInner(instance.SerializeCount, context);
                int deserializeCount = (int)SerializationManager.DeepCopyInner(instance.DeserializeCount, context);

                return new ReportingPOCO { A = a, B = b, CopyCount = copyCount + 1, SerializeCount = serializeCount, DeserializeCount = deserializeCount };
            }

            [SerializerMethod]
            internal static void Serialize(object input, ISerializationContext context, Type expected)
            {
                ReportingPOCO instance = (ReportingPOCO)input;

                SerializationManager.SerializeInner(instance.A, context, typeof(int));
                SerializationManager.SerializeInner(instance.B, context, typeof(int));
                SerializationManager.SerializeInner(instance.CopyCount, context, typeof(int));
                SerializationManager.SerializeInner(instance.SerializeCount + 1, context, typeof(int));
                SerializationManager.SerializeInner(instance.DeserializeCount, context, typeof(int));
            }

            [DeserializerMethod]
            internal static object Deserialize(Type expected, IDeserializationContext context)
            {
                int a = SerializationManager.DeserializeInner<int>(context);
                int b = SerializationManager.DeserializeInner<int>(context);
                int copyCount = SerializationManager.DeserializeInner<int>(context);
                int serializeCount = SerializationManager.DeserializeInner<int>(context);
                int deserializeCount = SerializationManager.DeserializeInner<int>(context);

                return new ReportingPOCO { A = a, B = b, CopyCount = copyCount, SerializeCount = serializeCount, DeserializeCount = deserializeCount + 1 };
            }
        }


        private readonly EventSourceEnvironmentFixture fixture;

        public SerializationTestsJsonTypes(EventSourceEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Serialization")]
        public void SerializationTests_GrainStateWithMetaDataAndETag_SerializableView()
        {
            GrainStateWithMetaDataAndETag<SimplePOCO> input = new GrainStateWithMetaDataAndETag<SimplePOCO>(new SimplePOCO { A = 1, B = 2 });

            GrainStateWithMetaDataAndETag<SimplePOCO> output = fixture.RoundTripSerialization(input);

            Assert.Equal(input.ToString(), output.ToString());
        }

        [Fact, TestCategory("Serialization")]
        public void SerializationTests_GrainStateWithMetaDataAndETag_NonSerializableView()
        {
            GrainStateWithMetaDataAndETag<AdvancedPOCO> input = new GrainStateWithMetaDataAndETag<AdvancedPOCO>(new AdvancedPOCO { A = 1, B = 2 });

            GrainStateWithMetaDataAndETag<AdvancedPOCO> output = fixture.RoundTripSerialization(input);

            Assert.Equal(input.ToString(), output.ToString());
        }


        [Fact, TestCategory("Serialization")]
        public void SerializationTests_GrainStateWithMetaDataAndETag_CopySerializableView()
        {
            GrainStateWithMetaDataAndETag<SimplePOCO> input = new GrainStateWithMetaDataAndETag<SimplePOCO>("eTag", new SimplePOCO { A = 1, B = 2 }, 1, "writeVector");

            GrainStateWithMetaDataAndETag<SimplePOCO> output = (GrainStateWithMetaDataAndETag<SimplePOCO>)fixture.SerializationManager.DeepCopy(input);

            Assert.Equal(input.ToString(), output.ToString());
        }

        [Fact, TestCategory("Serialization")]
        public void SerializationTests_GrainStateWithMetaDataAndETag_CopyNonSerializableView()
        {
            GrainStateWithMetaDataAndETag<AdvancedPOCO> input = new GrainStateWithMetaDataAndETag<AdvancedPOCO>("eTag", new AdvancedPOCO { A = 1, B = 2 }, 1, "writeVector");

            GrainStateWithMetaDataAndETag<AdvancedPOCO> output = (GrainStateWithMetaDataAndETag<AdvancedPOCO>)fixture.SerializationManager.DeepCopy(input);

            Assert.Equal(input.ToString(), output.ToString());
        }

        [Fact, TestCategory("Serialization")]
        public void SerializationTests_GrainStateWithMetaDataAndETag_CustomSerialization()
        {
            GrainStateWithMetaDataAndETag<ReportingPOCO> input = new GrainStateWithMetaDataAndETag<ReportingPOCO>(new ReportingPOCO { A = 1, B = 2 });

            GrainStateWithMetaDataAndETag<ReportingPOCO> output = fixture.RoundTripSerialization(input);

            Assert.Equal(0, output.State.CopyCount);
            Assert.Equal(1, output.State.SerializeCount);
            Assert.Equal(1, output.State.DeserializeCount);
        }
    }
}
