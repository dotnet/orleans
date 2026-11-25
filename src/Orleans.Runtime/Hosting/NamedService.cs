using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Hosting
{
    internal class NamedService<TService>(string name, Type implementationType)
    {
        public string Name { get; } = name;
        public Type ImplementationType { get; } = implementationType;
        public ServiceDescriptor? LifecycleParticipant { get; set; }
    }
}
