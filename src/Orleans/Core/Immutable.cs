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

using Orleans.Serialization;

namespace Orleans.Concurrency
{
    /// <summary>
    /// Wrapper class for carrying immutable data.
    /// </summary>
    /// <remarks>
    /// Objects that are known to be immutable are given special fast-path handling by the Orleans serializer 
    /// -- which in a nutshell allows the DeepCopy step to be skipped during message sends where the sender and reveiving grain are in the same silo.
    /// 
    /// One very common usage pattern for Immutable is when passing byte[] parameters to a grain. 
    /// If a program knows it will not alter the contents of the byte[] (for example, if it contains bytes from a static image file read from disk)
    /// then considerable savings in memory usage and message throughput can be obtained by marking that byte[] argument as <c>Immutable</c>.
    /// </remarks>
    /// <typeparam name="T">Type of data to be wrapped by this Immutable</typeparam>
    public struct Immutable<T>
    {
        private readonly T value;

        /// <summary> Return reference to the original value stored in this Immutable wrapper. </summary>
        public T Value { get { return value; } }

        /// <summary>
        /// Constructor to wrap the specified data object in new Immutable wrapper.
        /// </summary>
        /// <param name="value">Value to be wrapped and marked as immutable.</param>
        public Immutable(T value)
        {
            this.value = value;
        }

        /// <summary>
        /// Create a deep copy of the original value stored in this Immutable wrapper.
        /// </summary>
        /// <returns></returns>
        public T GetCopy()
        {
            return (T)SerializationManager.DeepCopy(Value);
        }
    }

    /// <summary>
    /// Utility class to add the .AsImmutable method to all objects.
    /// </summary>
    public static class ImmutableExt
    {
        /// <summary>
        /// Extension method to return this value wrapped in <c>Immutable</c>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value">Value to be wrapped.</param>
        /// <returns>Immutable wrapper around the original object.</returns>
        /// <seealso cref="Immutable{T}"/>"/>
        public static Immutable<T> AsImmutable<T>(this T value)
        {
            return new Immutable<T>(value);
        }
    }
}