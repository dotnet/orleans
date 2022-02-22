using System;
using Orleans.Runtime;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans
{
    /// <summary>
    /// Extension methods for grains.
    /// </summary>
    public static class GrainExtensions
    {
        private const string WRONG_GRAIN_ERROR_MSG = "Passing a half baked grain as an argument. It is possible that you instantiated a grain class explicitly, as a regular object and not via Orleans runtime or via proper test mocking";

        /// <summary>
        /// Returns a reference to the provided grain.
        /// </summary>
        /// <param name="grain">The grain to create a reference for.</param>
        /// <returns>A reference to the provided grain.</returns>
        internal static GrainReference AsReference(this IAddressable grain)
        {
            if (grain is null)
            {
                ThrowGrainNull();
            }

            // When called against an instance of a grain reference class, do nothing
            var reference = grain as GrainReference;
            if (reference != null) return reference;

            var context = grain switch
            {
                Grain grainBase => grainBase.GrainContext,
                IGrainBase activation => activation.GrainContext,
                ISystemTargetBase systemTarget => systemTarget,
                _ => throw new ArgumentException(GetWrongGrainTypeErrorMessage(grain), nameof(grain))
            };

            if (context?.GrainReference is not { } grainRef)
            {
                throw new ArgumentException(WRONG_GRAIN_ERROR_MSG, nameof(grain));
            }

            return grainRef;
        }

        /// <summary>
        /// Returns a typed reference to the provided grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The type of the grain interface.</typeparam>
        /// <param name="grain">The grain to convert.</param>
        /// <remarks>
        /// If the provided value is a grain instance, this will create a reference which implements the provided interface.
        /// If the provided value is already grain reference, this will create a new reference which implements the provided interface.
        /// </remarks>
        /// <returns>A strongly typed reference to the provided grain which implements <typeparamref name="TGrainInterface"/>.</returns>
        public static TGrainInterface AsReference<TGrainInterface>(this IAddressable grain)
        {
            if (grain is null)
            {
                ThrowGrainNull();
            }

            var grainReference = grain.AsReference();
            return (TGrainInterface)grainReference.Runtime.Cast(grain, typeof(TGrainInterface));
        }

        /// <summary>
        /// Returns a typed reference to the provided grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The type of the grain interface.</typeparam>
        /// <param name="grain">The grain to convert.</param>
        /// <remarks>
        /// This method is equivalent to <see cref="AsReference{TGrainInterface}"/>.
        /// If the provided value is a grain instance, this will create a reference which implements the provided interface.
        /// If the provided value is already grain reference, this will create a new reference which implements the provided interface.
        /// </remarks>
        /// <returns>A strongly typed reference to the provided grain which implements <typeparamref name="TGrainInterface"/>.</returns>
        public static TGrainInterface Cast<TGrainInterface>(this IAddressable grain) => grain.AsReference<TGrainInterface>();

        /// <summary>
        /// Returns a typed reference to the provided grain.
        /// </summary>
        /// <param name="grain">The grain to convert.</param>
        /// <param name="interfaceType">The type of the grain interface.</param>
        /// <remarks>
        /// If the provided value is a grain instance, this will create a reference which implements the provided interface.
        /// If the provided value is already grain reference, this will create a new reference which implements the provided interface.
        /// </remarks>
        /// <returns>A strongly typed reference to the provided grain which implements <paramref name="interfaceType"/>.</returns>
        public static object AsReference(this IAddressable grain, Type interfaceType) => grain.AsReference().Runtime.Cast(grain, interfaceType);

        /// <summary>
        /// Returns a typed reference to the provided grain.
        /// </summary>
        /// <param name="grain">The grain to convert.</param>
        /// <param name="interfaceType">The type of the grain interface.</param>
        /// <remarks>
        /// This method is equivalent to <see cref="AsReference(IAddressable, Type)"/>.
        /// If the provided value is a grain instance, this will create a reference which implements the provided interface.
        /// If the provided value is already grain reference, this will create a new reference which implements the provided interface.
        /// </remarks>
        /// <returns>A strongly typed reference to the provided grain which implements <paramref name="interfaceType"/>.</returns>
        public static object Cast(this IAddressable grain, Type interfaceType) => grain.AsReference().Runtime.Cast(grain, interfaceType);

        /// <summary>
        /// Returns the grain id corresponding to the provided grain.
        /// </summary>
        /// <param name="grain">The grain</param>
        /// <returns>The grain id corresponding to the provided grain.</returns>
        /// <exception cref="ArgumentException">The provided value has the wrong type or has no id.</exception>
        public static GrainId GetGrainId(this IAddressable grain)
        {
            var grainId = grain switch
            {
                Grain grainBase => grainBase.GrainId,
                GrainReference grainReference => grainReference.GrainId,
                IGrainBase grainActivation => grainActivation.GrainContext.GrainId,
                ISystemTargetBase systemTarget => systemTarget.GrainId,
                _ => throw new ArgumentException(GetWrongGrainTypeErrorMessage(grain), nameof(grain))
            };

            if (grainId.IsDefault)
            {
                throw new ArgumentException(WRONG_GRAIN_ERROR_MSG, nameof(grain));
            }

            return grainId;
        }

        /// <summary>
        /// Gets the exception message which is thrown when a grain argument has a non-supported implementation type.
        /// </summary>
        /// <param name="grain">The argument.</param>
        /// <returns>The exception message which is thrown when a grain argument has a non-supported implementation type.</returns>
        private static string GetWrongGrainTypeErrorMessage(IAddressable grain) =>
            $"{nameof(GetGrainId)} has been called on an unexpected type: {grain.GetType().FullName}."
            + $" If the parameter is a grain implementation, you can derive from {nameof(Grain)} or implement"
            + $" {nameof(IGrainBase)} in order to support this method. Alternatively, inject {nameof(IGrainContext)} and"
            + $" access the {nameof(IGrainContext.GrainId)} or {nameof(IGrainContext.GrainReference)} property.";

        /// <summary>
        /// Returns whether part of the primary key is of type <see langword="long"/>.
        /// </summary>
        /// <param name="grain">The target grain.</param>
        /// <exception cref="InvalidOperationException">The provided grain does not have a <see cref="long"/>-based key.</exception>
        public static bool IsPrimaryKeyBasedOnLong(this IAddressable grain)
        {
            var grainId = GetGrainId(grain);
            if (grainId.TryGetIntegerKey(out _))
            {
                return true;
            }

            if (LegacyGrainId.TryConvertFromGrainId(grainId, out var legacyId))
            {
                return legacyId.IsLongKey;
            }

            throw new InvalidOperationException($"Unable to extract integer key from grain id {grainId}");
        }

        /// <summary>
        /// Returns the <see langword="long"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
        /// <returns>A <see langword="long"/> representing the primary key for this grain.</returns>
        /// <exception cref="InvalidOperationException">The provided grain does not have a <see cref="long"/>-based key.</exception>
        public static long GetPrimaryKeyLong(this IAddressable grain, out string keyExt)
        {
            var grainId = GetGrainId(grain);
            if (grainId.TryGetIntegerKey(out var primaryKey, out keyExt))
            {
                return primaryKey;
            }

            if (LegacyGrainId.TryConvertFromGrainId(grainId, out var legacyId))
            {
                return legacyId.GetPrimaryKeyLong(out keyExt);
            }

            throw new InvalidOperationException($"Unable to extract integer key from grain id {grainId}");
        }

        /// <summary>
        /// Returns the <see langword="long"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <returns>A <see langword="long"/> representing the primary key for this grain.</returns>
        /// <exception cref="InvalidOperationException">The provided grain does not have a <see cref="long"/>-based key.</exception>
        public static long GetPrimaryKeyLong(this IAddressable grain)
        {
            var grainId = GetGrainId(grain);
            if (grainId.TryGetIntegerKey(out var primaryKey))
            {
                return primaryKey;
            }

            if (LegacyGrainId.TryConvertFromGrainId(grainId, out var legacyId))
            {
                return legacyId.GetPrimaryKeyLong();
            }

            throw new InvalidOperationException($"Unable to extract integer key from grain id {grainId}");
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
        /// <returns>A <see cref="Guid"/> representing the primary key for this grain.</returns>
        /// <exception cref="InvalidOperationException">The provided grain does not have a <see cref="Guid"/>-based key.</exception>
        public static Guid GetPrimaryKey(this IAddressable grain, out string keyExt)
        {
            var grainId = GetGrainId(grain);
            if (grainId.TryGetGuidKey(out var guid, out keyExt))
            {
                return guid;
            }

            if (LegacyGrainId.TryConvertFromGrainId(grainId, out var legacyId))
            {
                return legacyId.GetPrimaryKey(out keyExt);
            }

            if (grainId.TryGetIntegerKey(out var integerKey, out keyExt))
            {
                var N1 = integerKey;
                return new Guid(0, 0, 0, (byte)N1, (byte)(N1 >> 8), (byte)(N1 >> 16), (byte)(N1 >> 24), (byte)(N1 >> 32), (byte)(N1 >> 40), (byte)(N1 >> 48), (byte)(N1 >> 56));
            }

            throw new InvalidOperationException($"Unable to extract GUID key from grain id {grainId}");
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <returns>A <see cref="Guid"/> representing the primary key for this grain.</returns>
        /// <exception cref="InvalidOperationException">The provided grain does not have a <see cref="Guid"/>-based key.</exception>
        public static Guid GetPrimaryKey(this IAddressable grain)
        {
            var grainId = GetGrainId(grain);
            if (grainId.TryGetGuidKey(out var guid))
                return guid;

            if (LegacyGrainId.TryConvertFromGrainId(grainId, out var legacyId))
                return legacyId.GetPrimaryKey();

            if (grainId.TryGetIntegerKey(out var integerKey))
            {
                var N1 = integerKey;
                return new Guid(0, 0, 0, (byte)N1, (byte)(N1 >> 8), (byte)(N1 >> 16), (byte)(N1 >> 24), (byte)(N1 >> 32), (byte)(N1 >> 40), (byte)(N1 >> 48), (byte)(N1 >> 56));
            }

            throw new InvalidOperationException($"Unable to extract GUID key from grain id {grainId}");
        }

        /// <summary>
        /// Returns the <see langword="string"/> primary key of the grain.
        /// </summary>
        /// <param name="grain">The grain to find the primary key for.</param>
        /// <returns>A <see langword="string"/> representing the primary key for this grain.</returns>
        public static string GetPrimaryKeyString(this IAddressable grain)
        {
            var grainId = GetGrainId(grain);
            if (LegacyGrainId.TryConvertFromGrainId(grainId, out var legacyId))
            {
                return legacyId.GetPrimaryKeyString();
            }

            return grainId.Key.ToStringUtf8();
        }

        /// <summary>
        /// Throw an <see cref="ArgumentNullException"/> indicating that the grain argument is null.
        /// </summary>
        /// <exception cref="ArgumentNullException">The grain argument is null.</exception>
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowGrainNull() => throw new ArgumentNullException("grain");
    }
}
