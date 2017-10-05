using System;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    public static class SerializerContextExtensions
    {
        public static SerializationManager GetSerializationManager(this ISerializerContext context)
        {
            return (SerializationManager)context.ServiceProvider.GetService(typeof(SerializationManager));
        }
    }

    public abstract class SerializationContextBase : ISerializerContext
    {
        private class StreamlineServiceProvider : IServiceProvider
        {
            private SerializationManager serializationManager;

            public StreamlineServiceProvider(SerializationManager serializationManager)
            {
                this.serializationManager = serializationManager;
            }

            public object GetService(Type serviceType)
            {
                if (serviceType == typeof(SerializationManager))
                    return this.serializationManager;
                if (serviceType == typeof(IGrainReferenceRuntime))
                    return this.serializationManager.RuntimeClient.GrainReferenceRuntime;

                return this.serializationManager.ServiceProvider.GetService(serviceType);
            }
        }

        public SerializationManager SerializationManager { get; }
        public IServiceProvider ServiceProvider { get; }
        public abstract object AdditionalContext { get; }

        protected SerializationContextBase(SerializationManager serializationManager)
        {
            this.SerializationManager = serializationManager;
            this.ServiceProvider = new StreamlineServiceProvider(serializationManager);
        }
    }
}