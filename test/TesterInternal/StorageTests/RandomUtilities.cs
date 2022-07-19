using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Utility function to provide random values to some tests.
    /// </summary>
    public static class RandomUtilities
    {
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
        /// A list of random generators being used. This list is read-only after construction.
        /// </summary>
        /// <remarks>The state, basically the <see cref="Range{T}"/> object isn't used currently.</remarks>
        private static Dictionary<Type, object> RandomGenerators { get; } = new Dictionary<Type, object>
        {
            [typeof(Guid)] = new Func<object, Guid>(_ => Guid.NewGuid()),
            [typeof(int)] = new Func<object, int>(_ => Random.Shared.Next()),
            [typeof(long)] = new Func<object, long>(_=> Random.Shared.NextInt64()),
            [typeof(string)] = new Func<object, string>(symbolSet =>
            {
                var count = ((Tuple<Range<long>, SymbolSet>)symbolSet).Item1.Start;
                var symbols = ((Tuple<Range<long>, SymbolSet>)symbolSet).Item2;
                var builder = new StringBuilder();
                for(long i = 0; i < count; ++i)
                {
                    var symbolRange = symbols.SetRanges[Random.Shared.Next(symbols.SetRanges.Count)];
                    builder.Append((char)Random.Shared.Next(symbolRange.Start, symbolRange.End));
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
            if(symbolSet == null)
            {
                throw new ArgumentNullException(nameof(symbolSet));
            }

            if(count < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "The count news to be more than zero.");
            }

            object randomGenerator = RandomGenerators[typeof(string)];
            return ((Func<object, string>)randomGenerator)(Tuple.Create(new Range<long>(count, count), symbolSet));
        }

        /// <summary>
        /// Get a random grain ID.
        /// </summary>
        /// <typeparam name="TGrainKey">The grain key type.</typeparam>
        /// <typeparam name="TGrainGeneric">The type of the generic part of a grain.</typeparam>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="symbolSet">symbolset to use.</param>
        /// <param name="symbolCount">number of symbols to take from symbol set.</param>
        /// <returns>Random value of the given type.</returns>
        /// <exception cref="ArgumentException"/>.
        internal static GrainId GetRandomGrainId<TGrainKey, TGrainGeneric>(SymbolSet symbolSet, long symbolCount = 15)
        {
            if (symbolSet == null)
            {
                throw new ArgumentNullException(nameof(symbolSet));
            }

            if (typeof(TGrainKey) == typeof(string))
            {
                var grainId = GetRandomCharacters(symbolSet, symbolCount);
                return LegacyGrainId.FromParsableString(LegacyGrainId.GetGrainId(NormalGrainTypeCode, grainId).ToParsableString());
            }

            throw new ArgumentException(typeof(TGrainKey).Name);
        }

        /// <summary>
        /// Get a random grain ID.
        /// </summary>
        /// <param name="keyExtension">The grain extension key.</param>
        /// 
        /// <returns>Random value of the given type.</returns>
        /// <exception cref="ArgumentException"/>.
        internal static GrainId GetRandomGrainId<T>(bool keyExtension = false)
        {
            //If this a string type, some symbol set from which to draw the symbols needs to given
            //and a special kind of a parameter constructed.
            const long SymbolsDefaultCount = 15;
            var symbolSet = new SymbolSet(SymbolSet.Latin1);

            if (typeof(T)== typeof(Guid))
            {
                var extension = keyExtension ? GetRandomCharacters(symbolSet, SymbolsDefaultCount) : null;
                return LegacyGrainId.GetGrainId(UniqueKey.NewKey(Guid.NewGuid(), keyExtension ? UniqueKey.Category.KeyExtGrain : UniqueKey.Category.Grain, keyExtension ? KeyExtensionGrainTypeCode : NormalGrainTypeCode, extension));
            }

            if (typeof(T) == typeof(long))
            {
                var extension = keyExtension ? GetRandomCharacters(symbolSet, SymbolsDefaultCount) : null;
                return LegacyGrainId.GetGrainId(UniqueKey.NewKey(Random.Shared.NextInt64(), keyExtension ? UniqueKey.Category.KeyExtGrain : UniqueKey.Category.Grain, keyExtension ? KeyExtensionGrainTypeCode : NormalGrainTypeCode, extension));
            }

            if (typeof(T) == typeof(string))
            {
                var grainId = GetRandomCharacters(symbolSet, SymbolsDefaultCount);
                return LegacyGrainId.GetGrainId(NormalGrainTypeCode, grainId);
            }

            throw new ArgumentException(typeof(T).Name);
        }
    }
}
