// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a dependency injection container.
    /// </summary>
    public interface IDependencyResolver : IDependencyScope
    {
        /// <summary>
        /// Starts a resolution scope. Objects which are resolved in the given scope will belong to
        /// that scope, and when the scope is disposed, those objects are returned to the container.
        /// Implementers should return a new instance of <see cref="IDependencyScope"/> every time this
        /// method is called, unless the container does not have any concept of scope or resource
        /// release (in which case, it would be okay to return 'this', so long as the calls to
        /// <see cref="IDisposable.Dispose"/> are effectively NOOPs).
        /// </summary>
        /// <returns>The dependency scope.</returns>
        IDependencyScope BeginScope();
    }
}
