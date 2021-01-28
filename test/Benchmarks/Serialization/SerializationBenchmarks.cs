using System;
using System.Collections.Generic;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.ProtobufNet;
using UnitTests.GrainInterfaces;

namespace Benchmarks.Serialization
{
    public enum SerializerToUse
    {
        Default,
        IlBasedFallbackSerializer,
        ProtoBufNet
    }

    [MemoryDiagnoser]
    public class SerializationBenchmarks
    {
        private void InitializeSerializer(SerializerToUse serializerToUse)
        {
            Type fallback = null;
            switch (serializerToUse)
            {
                case SerializerToUse.Default:
                    break;
                case SerializerToUse.IlBasedFallbackSerializer:
                    fallback = typeof(ILBasedSerializer);
                    break;
                case SerializerToUse.ProtoBufNet:
                    fallback = typeof(ProtobufNetSerializer);
                    break;
                default:
                    throw new InvalidOperationException("Invalid Serializer was selected");
            }

            var client = new ClientBuilder()
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = nameof(SerializationBenchmarks);
                    options.ServiceId = Guid.NewGuid().ToString();
                })
                .Configure<SerializationProviderOptions>(
                    options => options.FallbackSerializationProvider = fallback)
                .Build();
            this.serializationManager = client.ServiceProvider.GetRequiredService<SerializationManager>();
        }
        
        [Params(SerializerToUse.IlBasedFallbackSerializer, SerializerToUse.Default, SerializerToUse.ProtoBufNet)]
        public SerializerToUse Serializer { get; set; }

        private OuterClass.SomeConcreteClass complexClass;

        private byte[] serializedBytes;

        private LargeTestData largeTestData;
        private SerializationManager serializationManager;

        [GlobalSetup]
        public void BenchmarkSetup()
        {
            this.InitializeSerializer(this.Serializer);

            this.complexClass = OuterClass.GetPrivateClassInstance();
            this.complexClass.Int = 89;
            this.complexClass.String = Guid.NewGuid().ToString();
            this.complexClass.NonSerializedInt = 39;
            var classes = new List<SomeAbstractClass>
            {
                this.complexClass,
                new AnotherConcreteClass
                {
                    AnotherString = "hi",
                    Interfaces = new List<ISomeInterface>
                    {
                        this.complexClass
                    }
                },
                new AnotherConcreteClass(),
                OuterClass.GetPrivateClassInstance()
            };
            
            this.complexClass.Classes = classes.ToArray();
            this.complexClass.Enum = SomeAbstractClass.SomeEnum.Something;
            this.complexClass.SetObsoleteInt(38);

            this.complexClass.Struct = new SomeStruct(10)
            {
                Id = Guid.NewGuid(),
                PublicValue = 6,
                ValueWithPrivateGetter = 7
            };
            this.complexClass.Struct.SetValueWithPrivateSetter(8);
            this.complexClass.Struct.SetPrivateValue(9);


            this.largeTestData = new LargeTestData
            {
                Description = "This is a test. This is only a test. In the event of a real execution, this would contain actual data.",
                EnumValue = TestEnum.First
            };
            this.largeTestData.SetBit(13);
            this.largeTestData.SetEnemy(17, CampaignEnemyTestType.Enemy1);

            this.serializedBytes = this.serializationManager.SerializeToByteArray(this.largeTestData);
        }

        [Benchmark]
        public byte[] SerializerBenchmark()
        {
            return this.serializationManager.SerializeToByteArray(this.largeTestData);
        }

        [Benchmark]
        public object DeserializerBenchmark()
        {
            return this.serializationManager.DeserializeFromByteArray<LargeTestData>(this.serializedBytes);
        }

        /// <summary>
        /// Performs a full serialization loop using a type which has not had code generation performed.
        /// </summary>
        /// <returns></returns>
        [Benchmark]
        public object FallbackFullLoop()
        {
            return OrleansSerializationLoop(this.complexClass);
        }

        internal object OrleansSerializationLoop(object input, bool includeWire = true)
        {
            var copy = this.serializationManager.DeepCopy(input);
            if (includeWire)
            {
                copy = this.serializationManager.RoundTripSerializationForTesting(copy);
            }
            return copy;
        }
    }

    [Serializable]
    internal struct SomeStruct
    {
        public Guid Id { get; set; }
        public int PublicValue { get; set; }
        public int ValueWithPrivateSetter { get; private set; }
        public int ValueWithPrivateGetter { private get; set; }
        private int PrivateValue { get; set; }
        public readonly int ReadonlyField;

        public SomeStruct(int readonlyField)
            : this()
        {
            this.ReadonlyField = readonlyField;
        }

        public int GetValueWithPrivateGetter()
        {
            return this.ValueWithPrivateGetter;
        }

        public int GetPrivateValue()
        {
            return this.PrivateValue;
        }

        public void SetPrivateValue(int value)
        {
            this.PrivateValue = value;
        }

        public void SetValueWithPrivateSetter(int value)
        {
            this.ValueWithPrivateSetter = value;
        }
    }

    internal interface ISomeInterface { int Int { get; set; } }

    [Serializable]
    internal abstract class SomeAbstractClass : ISomeInterface
    {
        [NonSerialized]
        private int nonSerializedIntField;

        public abstract int Int { get; set; }

        public List<ISomeInterface> Interfaces { get; set; }

        public SomeAbstractClass[] Classes { get; set; }

        [Obsolete("This field should not be serialized", true)]
        public int ObsoleteIntWithError { get; set; }

        [Obsolete("This field should be serialized")]
        public int ObsoleteInt { get; set; }


#pragma warning disable 618
        public int GetObsoleteInt() => this.ObsoleteInt;
        public void SetObsoleteInt(int value)
        {
            this.ObsoleteInt = value;
        }
#pragma warning restore 618

        public SomeEnum Enum { get; set; }

        public int NonSerializedInt
        {
            get
            {
                return this.nonSerializedIntField;
            }

            set
            {
                this.nonSerializedIntField = value;
            }
        }

        [Serializable]
        public enum SomeEnum
        {
            None,

            Something,

            SomethingElse
        }
    }

    internal class OuterClass
    {
        public static SomeConcreteClass GetPrivateClassInstance() => new PrivateConcreteClass(Guid.NewGuid());

        public static Type GetPrivateClassType() => typeof(PrivateConcreteClass);

        [Serializable]
        public class SomeConcreteClass : SomeAbstractClass
        {
            public override int Int { get; set; }

            public string String { get; set; }

            public SomeStruct Struct { get; set; }

            private PrivateConcreteClass secretPrivateClass;

            public void ConfigureSecretPrivateClass()
            {
                this.secretPrivateClass = new PrivateConcreteClass(Guid.NewGuid());
            }

            public bool AreSecretBitsIdentitcal(SomeConcreteClass other)
            {
                return other.secretPrivateClass?.Identity == this.secretPrivateClass?.Identity;
            }
        }

        [Serializable]
        private class PrivateConcreteClass : SomeConcreteClass
        {
            public PrivateConcreteClass(Guid identity)
            {
                this.Identity = identity;
            }

            public readonly Guid Identity;
        }
    }

    [Serializable]
    internal class AnotherConcreteClass : SomeAbstractClass
    {
        public override int Int { get; set; }

        public string AnotherString { get; set; }
    }

    [Serializable]
    internal class InnerType
    {
        public InnerType()
        {
            this.Id = Guid.NewGuid();
            this.Something = this.Id.ToString();
        }
        public Guid Id { get; set; }
        public string Something { get; set; }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((InnerType)obj);
        }

        protected bool Equals(InnerType other)
        {
            return this.Id.Equals(other.Id) && string.Equals(this.Something, other.Something);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (this.Id.GetHashCode() * 397) ^ (this.Something?.GetHashCode() ?? 0);
            }
        }
    }
}
