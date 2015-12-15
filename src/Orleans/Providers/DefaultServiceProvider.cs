using System;

namespace Orleans.Providers
{
    /// <summary>
    /// Default service provider.
    /// This should be replaced with a minimal Dependency Injection system, once a stable version is available.
    /// </summary>
    public class DefaultServiceProvider : IServiceProvider
    {
        public object GetService(Type serviceType)
        {
            return Activator.CreateInstance(serviceType);
        }
    }
}
