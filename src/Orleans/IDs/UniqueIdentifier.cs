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

namespace Orleans.Runtime
{
    [Serializable]
    internal abstract class UniqueIdentifier : IEquatable<UniqueIdentifier>, IComparable<UniqueIdentifier>
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        protected readonly internal UniqueKey Key;

        protected UniqueIdentifier()
        { }

        protected UniqueIdentifier(UniqueKey key)
        {
            Key = key;
        }

        public override string ToString()
        {
            return Key.ToString();
        }

        public override bool Equals(object obj)
        {
            var other = obj as UniqueIdentifier;
            return other != null && GetType() == other.GetType() && Key.Equals(other.Key);
        }

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }

        public uint GetHashCode_Modulo(uint umod)
        {
            int key = Key.GetHashCode();
            int mod = (int)umod;
            key = ((key % mod) + mod) % mod; // key should be positive now. So assert with checked.
            return checked((uint)key);
        }

        #region IEquatable<UniqueIdentifier> Members

        public virtual bool Equals(UniqueIdentifier other)
        {
            return other != null && GetType() == other.GetType() && Key.Equals(other.Key);
        }

        #endregion

        #region IComparable<UniqueIdentifier> Members

        public int CompareTo(UniqueIdentifier other)
        {
            return Key.CompareTo(other.Key);
        }

        #endregion
    }
}
