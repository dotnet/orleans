using System;
using Orleans;
using System.Collections;
using System.Collections.Generic;
using Orleans.Runtime;


namespace UnitTests.StorageTests.Relational.TestDataSets
{
    /// <summary>
    /// A set of simple test data set wit and without extension keys.
    /// </summary>
    /// <typeparam name="TGrainKey">The grain type (integer, guid or string)</typeparam>.
    public sealed class StorageDataSetPlain<TGrainKey>: IEnumerable<object[]>
    {
        /// <summary>
        /// The symbol set this data set uses.
        /// </summary>
        private static SymbolSet Symbols { get; } = new SymbolSet(SymbolSet.Latin1);

        /// <summary>
        /// The length of random string drawn form <see cref="Symbols"/>.
        /// </summary>
        private const long StringLength = 15L;


        private IEnumerable<object[]> DataSet { get; } = new[]
        {
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory, extensionKey: false)),
                new GrainState<TestState1> { State = new TestState1 { A = RandomUtilities.GetRandomCharacters(Symbols, StringLength), B = 1, C = 4 } }
            },
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory, true)),
                new GrainState<TestState1> { State = new TestState1 { A = RandomUtilities.GetRandomCharacters(Symbols, StringLength), B = 2, C = 5 } }
            },
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory, true)),
                new GrainState<TestState1> { State = new TestState1 { A = RandomUtilities.GetRandomCharacters(Symbols, StringLength), B = 3, C = 6 } }
            }
        };

        public IEnumerator<object[]> GetEnumerator()
        {
            return DataSet.GetEnumerator();
        }


        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
