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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;

namespace Orleans
{

    using Orleans.Extensions;

    public sealed class ConcurrentHashSet<T> : 
        ISerializable, 
        IDeserializationCallback, 
        ISet<T>
    {
        private readonly HashSet<T> hashSet = new HashSet<T>();
        private readonly ReaderWriterLockSlim rwl = new ReaderWriterLockSlim();
         
        public ReaderWriterLockSlim Lock {  get {  return rwl; } }
        
        public IEnumerator<T> GetEnumerator()
        {
            return hashSet.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        public void Add(T item)
        {
            rwl.WriteLockExecute(() => hashSet.Add(item));
        }

        public void UnionWith(IEnumerable<T> other)
        {
            rwl.WriteLockExecute(() => hashSet.UnionWith(other));
        }

        public void IntersectWith(IEnumerable<T> other)
        {
            rwl.WriteLockExecute(() => hashSet.IntersectWith(other));
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            rwl.WriteLockExecute(() => hashSet.ExceptWith(other));
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            rwl.WriteLockExecute(() => hashSet.SymmetricExceptWith(other));
        }

        public bool IsSubsetOf(IEnumerable<T> other)
        {
            return rwl.ReadLockExecute(() => hashSet.IsSubsetOf(other));
        }

        public bool IsSupersetOf(IEnumerable<T> other)
        {
            return rwl.ReadLockExecute(() => hashSet.IsSupersetOf(other));
        }

        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            return rwl.ReadLockExecute(() => hashSet.IsProperSupersetOf(other));
        }

        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            return rwl.ReadLockExecute(() => hashSet.IsProperSubsetOf(other));
        }

        public bool Overlaps(IEnumerable<T> other)
        {
            return rwl.ReadLockExecute(() => hashSet.Overlaps(other));
        }

        public bool SetEquals(IEnumerable<T> other)
        {
            return rwl.ReadLockExecute(() => hashSet.SetEquals(other));
        }

        bool ISet<T>.Add(T item)
        {
            return rwl.WriteLockExecute(() => ((ISet<T>)hashSet).Add(item));
        }

        public void Clear()
        {
            rwl.WriteLockExecute(hashSet.Clear);
        }

        public bool Contains(T item)
        {
            return rwl.ReadLockExecute(() => hashSet.Contains(item));
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            rwl.ReadLockExecute(() => hashSet.CopyTo(array, arrayIndex));
        }

        public bool Remove(T item)
        {
            return rwl.WriteLockExecute(() => hashSet.Remove(item));
        }

        int ICollection<T>.Count { get { return rwl.ReadLockExecute(() => ((ICollection<T>)hashSet).Count); } }

        public bool IsReadOnly { get { return rwl.ReadLockExecute(() => ((ICollection<T>)hashSet).IsReadOnly); } }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            hashSet.GetObjectData(info, context);
        }

        public void OnDeserialization(object sender)
        {
            hashSet.OnDeserialization(sender);
        }
    }
}
