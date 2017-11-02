namespace UnitTests.Grains
{
    using System;
    using System.Threading.Tasks;

    using Orleans;
    using Orleans.Runtime;
    using Orleans.Serialization;

    using UnitTests.GrainInterfaces;
    
    public class MessageSerializationGrain : Grain, IMessageSerializationGrain
    {
        public Task<object> EchoObject(object input) => Task.FromResult(input);

        public async Task GetUnserializableObjectChained()
        {
            // Find a grain on another silo.
            IMessageSerializationGrain otherGrain;
            var id = this.GetPrimaryKeyLong();
            var currentSiloIdentity = await this.GetSiloIdentity();
            while (true)
            {
                otherGrain = this.GrainFactory.GetGrain<IMessageSerializationGrain>(++id);
                var otherIdentity = await otherGrain.GetSiloIdentity();
                if (!string.Equals(otherIdentity, currentSiloIdentity))
                {
                    break;
                }
            }

            // Message that grain in a way which should fail.
            await otherGrain.EchoObject(new SimpleType(35));
        }

        public Task<string> GetSiloIdentity()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }
    }

    [Serializable]
    public struct SimpleType
    {
        public SimpleType(int num)
        {
            this.Number = num;
        }

        public int Number { get; }
    }

    /// <summary>
    /// Serializer which can serialize <see cref="SimpleType"/> but cannot deserialize it.
    /// </summary>
    [Serializable]
    public class OneWaySerializer : IExternalSerializer
    {
        public const string FailureMessage = "Can't do it, sorry.";

        public bool IsSupportedType(Type itemType) => itemType == typeof(SimpleType);

        public object DeepCopy(object source, ICopyContext context)
        {
            return source;
        }

        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            var typed = (SimpleType)item;
            context.StreamWriter.Write(typed.Number);
        }

        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            throw new NotSupportedException(FailureMessage);
        }
    }
}