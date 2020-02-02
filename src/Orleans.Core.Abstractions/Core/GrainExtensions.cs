using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Extension methods for grains.
    /// </summary>
    public static class GrainExtensions
    {
        private const string WRONG_GRAIN_ERROR_MSG = "Passing a half baked grain as an argument. It is possible that you instantiated a grain class explicitly, as a regular object and not via Orleans runtime or via proper test mocking";

        internal static GrainReference AsWeaklyTypedReference(this IAddressable grain)
        {
            ThrowIfNullGrain(grain);

            // When called against an instance of a grain reference class, do nothing
            var reference = grain as GrainReference;
            if (reference != null) return reference;

            var grainBase = grain as Grain;
            if (grainBase != null)
            {
                if (grainBase.Data?.GrainReference == null)
                {
                    throw new ArgumentException(WRONG_GRAIN_ERROR_MSG, nameof(grain));
                }

                return grainBase.Data.GrainReference;
            }

            var systemTarget = grain as ISystemTargetBase;
            if (systemTarget != null) return GrainReference.FromGrainId(systemTarget.GrainId, systemTarget.GrainReferenceRuntime, null, systemTarget.Silo);

            throw new ArgumentException(
                $"AsWeaklyTypedReference has been called on an unexpected type: {grain.GetType().FullName}.",
                nameof(grain));
        }

        /// <summary>
        /// Converts this grain to a specific grain interface.
        /// </summary>
        /// <typeparam name="TGrainInterface">The type of the grain interface.</typeparam>
        /// <param name="grain">The grain to convert.</param>
        /// <returns>A strongly typed <c>GrainReference</c> of grain interface type TGrainInterface.</returns>
        public static TGrainInterface AsReference<TGrainInterface>(this IAddressable grain)
        {
            ThrowIfNullGrain(grain);
            var grainReference = grain.AsWeaklyTypedReference();
            return grainReference.Runtime.Convert<TGrainInterface>(grainReference);
        }

        /// <summary>
        /// Casts a grain to a specific grain interface.
        /// </summary>
        /// <typeparam name="TGrainInterface">The type of the grain interface.</typeparam>
        /// <param name="grain">The grain to cast.</param>
        public static TGrainInterface Cast<TGrainInterface>(this IAddressable grain)
        {
            return grain.AsReference<TGrainInterface>();
        }

        /// <summary>
        /// Casts the provided <paramref name="grain"/> to the provided <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="interfaceType">The resulting interface type.</param>
        /// <returns>A reference to <paramref name="grain"/> which implements <paramref name="interfaceType"/>.</returns>
        public static object Cast(this IAddressable grain, Type interfaceType)
        {

            return grain.AsWeaklyTypedReference().Runtime.Convert(grain, interfaceType);
        }

        /// <summary>
        /// Binds the grain reference to the provided <see cref="IGrainFactory"/>.
        /// </summary>
        /// <param name="grain">The grain reference.</param>
        /// <param name="grainFactory">The grain factory.</param>
        public static void BindGrainReference(this IAddressable grain, IGrainFactory grainFactory)
        {
            grainFactory.BindGrainReference(grain);
        }

        internal static GrainId GetGrainId(IAddressable grain)
        {
            var reference = grain as GrainReference;
            if (reference != null)
            {
                if (reference.GrainId == null)
                {
                    throw new ArgumentException(WRONG_GRAIN_ERROR_MSG, "grain");
                }
                return reference.GrainId;
            }

            var grainBase = grain as Grain;
            if (grainBase != null)
            {
                if (grainBase.Data == null || grainBase.Data.Identity == null)
                {
                    throw new ArgumentException(WRONG_GRAIN_ERROR_MSG, "grain");
                }
                return grainBase.Data.Identity;
            }

            throw new ArgumentException(String.Format("GetGrainId has been called on an unexpected type: {0}.", grain.GetType().FullName), "grain");
        }

        public static IGrainIdentity GetGrainIdentity(this IGrain grain)
        {
            var grainBase = grain as Grain;
            if (grainBase != null)
            {
                if (grainBase.Identity == null)
                {
                    throw new ArgumentException(WRONG_GRAIN_ERROR_MSG, "grain");
                }
                return grainBase.Identity;
            }

            var grainReference = grain as GrainReference;
            if (grainReference != null)
            {
                if (grainReference.GrainId == null)
                {
                    throw new ArgumentException(WRONG_GRAIN_ERROR_MSG, "grain");
                }
                return grainReference.GrainId;
            }

            throw new ArgumentException(String.Format("GetGrainIdentity has been called on an unexpected type: {0}.", grain.GetType().FullName), "grain");
        }

        /// <summary>
        /// Returns whether part of the primary key is of type long.
        /// </summary>
        /// <param name="grain">The target grain.</param>
        public static bool IsPrimaryKeyBasedOnLong(this IAddressable grain)
        {
            return GetGrainId(grain).IsLongKey;
        }

        /// <summary>
        /// Returns the long representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
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
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
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

        /// <summary>
        /// Returns the string primary key of the grain.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <returns>A string representing the primary key for this grain.</returns>
        public static string GetPrimaryKeyString(this IAddressable grain)
        {
            return GetGrainId(grain).GetPrimaryKeyString();
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

        /// <summary>
        /// Invokes a method of a grain interface is one-way fashion so that no response message will be sent to the caller.
        /// </summary>
        /// <typeparam name="T">Grain interface</typeparam>
        /// <param name="grainReference">Grain reference which will be copied and then a call executed on it</param>
        /// <param name="grainMethodInvocation">Function that should invoke grain method and return resulting task</param>
        public static void InvokeOneWay<T>(this T grainReference, Func<T, Task> grainMethodInvocation) where T : class, IAddressable
        {
            var oneWayGrainReferenceCopy = new GrainReference(grainReference.AsWeaklyTypedReference(), InvokeMethodOptions.OneWay).Cast<T>();

            // Task is always completed at this point. Should also help to catch situations of mistakenly calling the method on original grain reference
            var invokationResult = grainMethodInvocation(oneWayGrainReferenceCopy);
            if (!invokationResult.IsCompleted)
            {
                throw new InvalidOperationException("Invoking of methods with one way flag must result in completed task");
            }
        }

        private static void ThrowIfNullGrain(IAddressable grain)
        {
            if (grain == null)
            {
                throw new ArgumentNullException(nameof(grain));
            }
        }
    }
}
