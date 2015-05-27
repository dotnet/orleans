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
using Orleans.Core;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Extension methods for grains.
    /// </summary>
    public static class GrainExtensions
    {
        private static GrainReference AsWeaklyTypedReference(this Runtime.IAddressable grain)
        {
            var reference = grain as Runtime.GrainReference;
            // When called against an instance of a grain reference class, do nothing
            if (reference != null) return reference;

            var grainBase = grain as Grain;
            if (grainBase != null)
                return ((Grain) grain).Data.GrainReference;

            var systemTarget = grain as ISystemTargetBase;
            if (systemTarget != null)
                return GrainReference.FromGrainId(systemTarget.GrainId, null, systemTarget.Silo);

            throw new OrleansException(String.Format("AsWeaklyTypedReference has been called on an unexpected type: {0}.", grain.GetType().FullName));
        }

        /// <summary>
        /// Converts this grain to a specific grain interface.
        /// </summary>
        /// <typeparam name="TGrainInterface">The type of the grain interface.</typeparam>
        /// <param name="grain">The grain to convert.</param>
        /// <returns>A strongly typed <c>GrainReference</c> of grain interface type TGrainInterface.</returns>
        public static TGrainInterface AsReference<TGrainInterface>(this Runtime.IAddressable grain)
        {
            return GrainFactory.Cast<TGrainInterface>(grain.AsWeaklyTypedReference());
        }

        /// <summary>
        /// Casts a grain to a specific grain interface.
        /// </summary>
        /// <typeparam name="TGrainInterface">The type of the grain interface.</typeparam>
        /// <param name="grain">The grain to cast.</param>
        public static TGrainInterface Cast<TGrainInterface>(this Runtime.IAddressable grain)
        {
            return GrainFactory.Cast<TGrainInterface>(grain);
        }

        internal static GrainId GetGrainId(IAddressable grain)
        {
            var reference = grain as GrainReference;
            if (reference != null) return reference.GrainId;

            var grainBase = grain as Grain;
            if (grainBase != null) return grainBase.Data.Identity;
            
            throw new OrleansException(String.Format("GetGrainId has been called on an unexpected type: {0}.", grain.GetType().FullName));
        }

        internal static IGrainIdentity GetGrainIdentity(IGrain grain)
        {
            var grainBase = grain as Grain;
            if (grainBase != null) return grainBase.Identity;

            var grainReference = grain as GrainReference;
            if (grainReference != null) return (grainReference.GrainId);

            throw new OrleansException(String.Format("GetGrainIdentity has been called on an unexpected type: {0}.", grain.GetType().FullName));
        }

        /// <summary>
        /// Returns the long representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output paramater to return the extended key part of the grain primary key, if extened primary key was provided for that grain.</param>
        /// <returns>A long representing the primary key for this grain.</returns>
        public static long GetPrimaryKeyLong(this IAddressable grain, out string keyExt)
        {
            return GetGrainId(grain).GetPrimaryKeyLong(out keyExt);
        }

        /// <summary>
        /// Returns the long representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <returns>A long representing the primary key for this grain.</returns>
        public static long GetPrimaryKeyLong(this IAddressable grain)
        {
            return GetGrainId(grain).GetPrimaryKeyLong();
        }
        /// <summary>
        /// Returns the Guid representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output paramater to return the extended key part of the grain primary key, if extened primary key was provided for that grain.</param>
        /// <returns>A Guid representing the primary key for this grain.</returns>
        public static Guid GetPrimaryKey(this IAddressable grain, out string keyExt)
        {
            return GetGrainId(grain).GetPrimaryKey(out keyExt);
        }

        /// <summary>
        /// Returns the Guid representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <returns>A Guid representing the primary key for this grain.</returns>
        public static Guid GetPrimaryKey(this IAddressable grain)
        {
            return GetGrainId(grain).GetPrimaryKey();
        }

        public static long GetPrimaryKeyLong(this IGrain grain, out string keyExt)
        {
            return GetGrainIdentity(grain).GetPrimaryKeyLong(out keyExt);
        }
        public static long GetPrimaryKeyLong(this IGrain grain)
        {
            return GetGrainIdentity(grain).PrimaryKeyLong;
        }
        public static Guid GetPrimaryKey(this IGrain grain, out string keyExt)
        {
            return GetGrainIdentity(grain).GetPrimaryKey(out keyExt);
        }
        public static Guid GetPrimaryKey(this IGrain grain)
        {
            return GetGrainIdentity(grain).PrimaryKey;
        }

        public static string GetPrimaryKeyString(this IGrainWithStringKey grain)
        {
            return GetGrainIdentity(grain).PrimaryKeyString;
        }
    }
}
