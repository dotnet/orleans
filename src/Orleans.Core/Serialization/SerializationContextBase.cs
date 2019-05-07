using System;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    public static class SerializerContextExtensions
    {
        public static SerializationManager GetSerializationManager(this ISerializerContext context)
        {
            if (context is SerializationContextBase common) return common.SerializationManager;
            return (SerializationManager)context.ServiceProvider.GetService(typeof(SerializationManager));
        }
    }

    public abstract class SerializationContextBase : ISerializerContext
    {
        public SerializationManager SerializationManager { get; }
        public IServiceProvider ServiceProvider => this.SerializationManager.ServiceProvider;
        public abstract object AdditionalContext { get; }

        protected SerializationContextBase(SerializationManager serializationManager)
        {
            this.SerializationManager = serializationManager;
        }
    }
}