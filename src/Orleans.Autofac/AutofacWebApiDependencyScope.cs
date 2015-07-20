using System;
using System.Collections.Generic;
using System.Security;
using System.Linq;
using Autofac;
using Orleans.Runtime;

namespace Orleans.Autofac
{
    /// <summary>
    /// Autofac implementation of the <see cref="IDependencyScope"/> interface.
    /// </summary>
    [SecurityCritical]
    public class AutofacWebApiDependencyScope : IDependencyScope
    {
        private bool _disposed;

        readonly ILifetimeScope _lifetimeScope;

        /// <summary>
        /// Initializes a new instance of the <see cref="AutofacWebApiDependencyScope"/> class.
        /// </summary>
        /// <param name="lifetimeScope">The lifetime scope to resolve services from.</param>
        public AutofacWebApiDependencyScope(ILifetimeScope lifetimeScope)
        {
            if (lifetimeScope == null) throw new ArgumentNullException("lifetimeScope");

            _lifetimeScope = lifetimeScope;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="AutofacWebApiDependencyScope"/> class.
        /// </summary>
        [SecuritySafeCritical]
        ~AutofacWebApiDependencyScope()
        {
            Dispose(false);
        }

        /// <summary>
        /// Gets the lifetime scope for the current dependency scope.
        /// </summary>
        public ILifetimeScope LifetimeScope
        {
            get { return _lifetimeScope; }
        }

        /// <summary>
        /// Try to get a service of the given type.
        /// </summary>
        /// <param name="serviceType">ControllerType of service to request.</param>
        /// <returns>An instance of the service, or null if the service is not found.</returns>
        [SecurityCritical]
        public object GetService(Type serviceType)
        {
            return _lifetimeScope.ResolveOptional(serviceType);
        }

        /// <summary>
        /// Try to get a list of services of the given type.
        /// </summary>
        /// <param name="serviceType">ControllerType of services to request.</param>
        /// <returns>An enumeration (possibly empty) of the service.</returns>
        [SecurityCritical]
        public IEnumerable<object> GetServices(Type serviceType)
        {
            if (!_lifetimeScope.IsRegistered(serviceType))
                return Enumerable.Empty<object>();

            var enumerableServiceType = typeof(IEnumerable<>).MakeGenericType(serviceType);
            var instance = _lifetimeScope.Resolve(enumerableServiceType);
            return (IEnumerable<object>)instance;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        [SecuritySafeCritical]
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_lifetimeScope != null)
                    {
                        _lifetimeScope.Dispose();
                    }
                }
                _disposed = true;
            }
        }
    }
}
