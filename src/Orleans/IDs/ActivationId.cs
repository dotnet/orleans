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

﻿using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class ActivationId : UniqueIdentifier, IEquatable<ActivationId>
    {
        public bool IsSystem { get { return Key.IsSystemTargetKey; } }

        public static readonly ActivationId Zero;

        private static readonly Interner<UniqueKey, ActivationId> interner;

        static ActivationId()
        {
            interner = new Interner<UniqueKey, ActivationId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq);
            Zero = FindOrCreate(UniqueKey.Empty);
        }

        /// <summary>
        /// Only used in Json serialization
        /// DO NOT USE TO CREATE A RANDOM ACTIVATION ID
        /// Use ActivationId.NewId to create new activation IDs.
        /// </summary>
        public ActivationId()
        {
        }

        private ActivationId(UniqueKey key)
            : base(key)
        {
        }

        public static ActivationId NewId()
        {
            return FindOrCreate(UniqueKey.NewKey());
        }

        // No need to encode SiloAddress in the activation address for system target. 
        // System targets have unique grain ids and addressed to a concrete silo, so in fact we don't need ActivationId at all for System targets.
        // Need to remove it all together. For now, just use grain id as activation id.
        public static ActivationId GetSystemActivation(GrainId grain, SiloAddress location)
        {
            if (!grain.IsSystemTarget)
                throw new ArgumentException("System activation IDs can only be created for system grains");
            return FindOrCreate(grain.Key);
        }

        internal static ActivationId GetActivationId(GrainId grain)
        {
            return FindOrCreate(grain.Key);
        }

        internal static ActivationId GetActivationId(UniqueKey key)
        {
            return FindOrCreate(key);
        }

        private static ActivationId FindOrCreate(UniqueKey key)
        {
            return interner.FindOrCreate(key, () => new ActivationId(key));
        }

        public override bool Equals(UniqueIdentifier obj)
        {
            var o = obj as ActivationId;
            return o != null && Key.Equals(o.Key);
        }

        public override bool Equals(object obj)
        {
            var o = obj as ActivationId;
            return o != null && Key.Equals(o.Key);
        }

        #region IEquatable<ActivationId> Members

        public bool Equals(ActivationId other)
        {
            return other != null && Key.Equals(other.Key);
        }

        #endregion

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }

        public override string ToString()
        {
            string idString = Key.ToString().Substring(24, 8);
            return String.Format("@{0}{1}", IsSystem ? "S" : "", idString);
        }

        public string ToFullString()
        {
            string idString = Key.ToString();
            return String.Format("@{0}{1}", IsSystem ? "S" : "", idString);
        }
    }
}
