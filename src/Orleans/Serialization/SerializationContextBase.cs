using System;

namespace Orleans.Serialization
{
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