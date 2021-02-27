using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Text;
using Orleans;
using Orleans.Internal;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Utility function to provide random values to some tests.
    /// </summary>
    public static class RandomUtilities
    {
        /// <summary>
        /// A sentinel value for when a generic parameter isn't used and applied to some function call.
        /// </summary>
        private class NotApplicable { };

        /// <summary>
        /// This type code is consistent with Orleans.Runtime.Category.Grain = 3, used also like
        /// "public Category IdCategory { get { return GetCategory(TypeCodeData); } }".
        /// Note that 0L would likely do also.
        /// </summary>
        public const long NormalGrainTypeCode = 3L;

        /// <summary>
        /// This type code is consistent with Orleans.Runtime.Category.Grain = 6, used also like
        /// "public Category IdCategory { get { return GetCategory(TypeCodeData); } }".
        /// Note that 0L would likely do also.
        /// </summary>
        public const long KeyExtensionGrainTypeCode = 6L;

        /// <summary>
        /// A rudimentary type switch that is read-only after construction and doesn't take into account inheritance etc. This is to simply
        /// some generic code that bridges calls to non-generic code.
        /// </summary>
        /// <remarks>This switch could take the key generator function as a parameter, so the called could use this same code to
        /// create known grain ID values.</remarks>
        private static Dictionary<Type, Func<IInternalGrainFactory, Type, bool, object, IAddressable>> GrainReferenceTypeSwitch { get; } = new Dictionary<Type, Func<IInternalGrainFactory, Type, bool, object, IAddressable>>
        {
            [typeof(Guid)] = (grainFactory, type, keyExtension, state) =>
            {
                var range = ((Tuple<Range<long>, SymbolSet>)state).Item1;
                var symbolSet = ((Tuple<Range<long>, SymbolSet>)state).Item2;
                var extension = keyExtension ? GetRandomCharacters(symbolSet, range) : null;

                Guid grainId = GetRandom<Guid>();
                if(type != typeof(NotApplicable))
                {
                    return grainFactory.GetGrain(LegacyGrainId.GetGrainId(UniqueKey.NewKey(grainId, keyExtension ? UniqueKey.Category.KeyExtGrain : UniqueKey.Category.Grain, keyExtension ? KeyExtensionGrainTypeCode : NormalGrainTypeCode, extension)));
                }

                return grainFactory.GetGrain(LegacyGrainId.GetGrainId(UniqueKey.NewKey(grainId, keyExtension ? UniqueKey.Category.KeyExtGrain : UniqueKey.Category.Grain, keyExtension ? KeyExtensionGrainTypeCode : NormalGrainTypeCode, extension)));
            },
            [typeof(long)] = (grainFactory, type, keyExtension, state) =>
            {
                var range = ((Tuple<Range<long>, SymbolSet>)state).Item1;
                var symbolSet = ((Tuple<Range<long>, SymbolSet>)state).Item2;
                var extension = keyExtension ? GetRandomCharacters(symbolSet, range) : null;

                long grainId = GetRandom<long>();
                if(type != typeof(NotApplicable))
                {
                    return grainFactory.GetGrain(GrainId.Create("faketype", grainId.ToString()));
                }

                return grainFactory.GetGrain(LegacyGrainId.GetGrainId(UniqueKey.NewKey(grainId, keyExtension ? UniqueKey.Category.KeyExtGrain : UniqueKey.Category.Grain, keyExtension ? KeyExtensionGrainTypeCode : NormalGrainTypeCode, extension)));
            },
            [typeof(string)] = (grainFactory, type, keyExtension, state) =>
            {
                var range = ((Tuple<Range<long> , SymbolSet>)state).Item1;
                var symbolSet = ((Tuple<Range<long>, SymbolSet>)state).Item2;

                var grainId = GetRandomCharacters(symbolSet, range);
                if(type != typeof(NotApplicable))
                {
                    return grainFactory.GetGrain(LegacyGrainId.FromParsableString(LegacyGrainId.GetGrainId(NormalGrainTypeCode, grainId).ToParsableString()));
                }

                return grainFactory.GetGrain(LegacyGrainId.GetGrainId(NormalGrainTypeCode, grainId).ToGrainId());
            }
        };

        /// <summary>
        /// A list of random generators being used. This list is read-only after construction.
        /// </summary>
        /// <remarks>The state, basically the <see cref="Range{T}"/> object isn't used currently.</remarks>
        private static Dictionary<Type, object> RandomGenerators { get; } = new Dictionary<Type, object>
        {
            [typeof(Guid)] = new Func<object, Guid>(state => { return Guid.NewGuid(); }),
            [typeof(int)] = new Func<object, int>(state => { return ThreadSafeRandom.Next(); }),
            [typeof(long)] = new Func<object, long>(state =>
            {
                var bufferInt64 = new byte[sizeof(long)];
                ThreadSafeRandom.NextBytes(bufferInt64);
                return BitConverter.ToInt64(bufferInt64, 0);
            }),
            [typeof(string)] = new Func<object, string>(symbolSet =>
            {
                var count = ((Tuple<Range<long>, SymbolSet>)symbolSet).Item1.Start;
                var symbols = ((Tuple<Range<long>, SymbolSet>)symbolSet).Item2;
                var builder = new StringBuilder();
                for(long i = 0; i < count; ++i)
                {
                    var symbolRange = symbols.SetRanges[ThreadSafeRandom.Next(symbols.SetRanges.Count)];
                    builder.Append((char)ThreadSafeRandom.Next(symbolRange.Start, symbolRange.End));
                }

                return builder.ToString();
            })
        };


        /// <summary>
        /// Get a random value of the given type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>Random value of the given type.</returns>
        /// <exception cref="ArgumentException"/>.
        public static T GetRandom<T>(Range<long> range = null)
        {
            object randomGenerator;
            if(RandomGenerators.TryGetValue(typeof(T), out randomGenerator))
            {
                //If this a string type, some symbol set from which to draw the symbols needs to given
                //and a special kind of a parameter constructed.
                if((typeof(T) == typeof(string)))
                {
                    const long SymbolsDefaultCount = 15;
                    var symbols = new SymbolSet(SymbolSet.Latin1);
                    return ((Func<object, T>)randomGenerator)(Tuple.Create(range ?? new Range<long>(SymbolsDefaultCount, SymbolsDefaultCount), symbols));
                }

                return ((Func<object, T>)randomGenerator)(null);
            }

            throw new ArgumentException(typeof(T).Name);
        }


        /// <summary>
        /// Get random symbols.
        /// </summary>
        /// <param name="symbolSet">The set of symbols from which the random characters are drawn from.</param>
        /// <param name="count">The count of random symbols.</param>
        /// <returns>A random string.</returns>
        /// <exception cref="ArgumentNullException"/>.
        /// <exception cref="ArgumentOutOfRangeException"/>.
        public static string GetRandomCharacters(SymbolSet symbolSet, long count)
        {
            return GetRandomCharacters(symbolSet, new Range<long>(count, count));
        }


        /// <summary>
        /// Get random symbols.
        /// </summary>
        /// <param name="symbolSet">The set of symbols from which the random characters are drawn from.</param>
        /// <param name="count">The count of random symbols.</param>
        /// <returns>A random string.</returns>
        /// <exception cref="ArgumentNullException"/>.
        /// <exception cref="ArgumentOutOfRangeException"/>.
        public static string GetRandomCharacters(SymbolSet symbolSet, Range<long> count)
        {
            if(symbolSet == null)
            {
                throw new ArgumentNullException(nameof(symbolSet));
            }

            if(count.Start < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "The count news to be more than zero.");
            }

            object randomGenerator = RandomGenerators[typeof(string)];
            return ((Func<object, string>)randomGenerator)(Tuple.Create(count, symbolSet));
        }


        /// <summary>
        /// Get a random grain reference.
        /// </summary>
        /// <typeparam name="TGrainKey">The grain key type.</typeparam>
        /// <typeparam name="TGrainGeneric">The type of the generic part of a grain.</typeparam>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="keyExtension">Should an extension key be defined or not.</param>
        /// <returns>Random value of the given type.</returns>
        /// <exception cref="ArgumentException"/>.
        internal static GrainReference GetRandomGrainReference<TGrainKey, TGrainGeneric>(IInternalGrainFactory grainFactory, bool keyExtension)
        {
            Func<IInternalGrainFactory, Type, bool, object, IAddressable> func;
            if(GrainReferenceTypeSwitch.TryGetValue(typeof(TGrainKey), out func))
            {
                //If this a string type, some symbol set from which to draw the symbols needs to given
                //and a special kind of a parameter constructed.
                const long SymbolsDefaultCount = 15;
                var symbols = new SymbolSet(SymbolSet.Latin1);

                return (GrainReference)func(grainFactory, typeof(TGrainGeneric), keyExtension, Tuple.Create(new Range<long>(SymbolsDefaultCount, SymbolsDefaultCount), symbols));
            }

            throw new ArgumentException(typeof(TGrainKey).Name);
        }


        /// <summary>
        /// Get a random grain reference.
        /// </summary>
        /// <typeparam name="TGrainKey">The grain key type.</typeparam>
        /// <typeparam name="TGrainGeneric">The type of the generic part of a grain.</typeparam>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="symbolSet">symbolset to use.</param>
        /// <param name="symbolCount">number of symbols to take from symbol set.</param>
        /// <param name="keyExtension">Should an extension key be defined or not.</param>
        /// <returns>Random value of the given type.</returns>
        /// <exception cref="ArgumentException"/>.
        internal static GrainReference GetRandomGrainReference<TGrainKey, TGrainGeneric>(IInternalGrainFactory grainFactory, SymbolSet symbolSet, long symbolCount = 15, bool keyExtension = false)
        {
            if(symbolSet == null)
            {
                throw new ArgumentNullException(nameof(symbolSet));
            }

            Func<IInternalGrainFactory, Type, bool, object, IAddressable> func;
            if(GrainReferenceTypeSwitch.TryGetValue(typeof(TGrainKey), out func))
            {
                return (GrainReference)func(grainFactory, typeof(TGrainGeneric), keyExtension, Tuple.Create(new Range<long>(symbolCount, symbolCount), symbolSet));
            }

            throw new ArgumentException(typeof(TGrainKey).Name);
        }


        /// <summary>
        /// Get a random grain reference.
        /// </summary>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="extensionKey">The grain extension key.</param>
        /// <returns>Random value of the given type.</returns>
        /// <exception cref="ArgumentException"/>.
        internal static GrainReference GetRandomGrainReference<T>(IInternalGrainFactory grainFactory, bool extensionKey = false)
        {
            return GetRandomGrainReference<T, NotApplicable>(grainFactory, extensionKey);
        }
    }
}
