/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
//using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a scope that is tracked by the dependency injection container. The scope is
    /// used to keep track of resources that have been provided, so that they can then be
    /// subsequently released when <see cref="IDisposable.Dispose"/> is called.
    /// </summary>
    public interface IDependencyScope : IDisposable
    {
        /// <summary>
        /// Gets an instance of the given <paramref name="serviceType"/>. Must return <c>null</c>
        /// if the service is not available (must not throw).
        /// </summary>
        /// <param name="serviceType">The object type.</param>
        /// <returns>The requested object, if found; <c>null</c> otherwise.</returns>
        object GetService(Type serviceType);

        //// <summary>
        //// Gets all instances of the given <paramref name="serviceType"/>. Must return an empty
        //// collection if the service is not available (must not return <c>null</c> or throw).
        //// </summary>
        //// <param name="serviceType">The object type.</param>
        //// <returns>A sequence of instances of the requested <paramref name="serviceType"/>. The sequence
        //// should be empty (not <c>null</c>) if no objects of the given type are available.</returns>
        
        // Currently not used, so not supported
        //IEnumerable<object> GetServices(Type serviceType);
    }
}
