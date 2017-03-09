using System;

namespace Orleans
{
    namespace Concurrency
    {
    }

    namespace MultiCluster
    {
    }

    namespace Placement
    {
    }

    namespace CodeGeneration
    {
    }

    namespace Providers
    {
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple=true)]
    public sealed class ImplicitStreamSubscriptionAttribute : Attribute
    {
        public string Namespace { get; private set; }
        
        // We have not yet come to an agreement whether the provider should be specified as well.
        public ImplicitStreamSubscriptionAttribute(string streamNamespace)
        {
            Namespace = streamNamespace;
        }
    }
}
