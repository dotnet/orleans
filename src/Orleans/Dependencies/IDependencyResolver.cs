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
