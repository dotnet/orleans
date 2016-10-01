using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;


namespace UnitTests.StorageTests.Relational.TestDataSets
{
    public static class GrainTypeGenerator
    {
        /// <summary>
        /// A sentinel value for when a generic parameter isn't used and applied to some function call.
        /// </summary>
        private class NotApplicable { };


        private interface ITestGrainWithIntegerKey: IGrainWithIntegerKey { }
        private interface ITestGrainGenericWithIntegerKey<T>: IGrainWithIntegerKey { }
        private class TestGrainWithIntegerKey: Grain, ITestGrainWithIntegerKey { }
        private class TestGrainGenericWithIntegerKey<T>: Grain, ITestGrainGenericWithIntegerKey<T> { }

        private interface ITestGrainWithGuidKey: IGrainWithGuidKey { }
        private interface ITestGrainGenericWithGuidKey<T>: IGrainWithGuidKey { }
        private class TestGrainWithGuidKey: Grain, ITestGrainWithGuidKey { }
        private class TestGrainGenericWithGuidKey<T>: Grain, ITestGrainGenericWithGuidKey<T> { }

        private interface ITestGrainWithStringKey: IGrainWithStringKey { }
        private interface ITestGrainGenericWithStringKey<T>: IGrainWithStringKey { }
        private class TestGrainWithStringKey: Grain, ITestGrainWithStringKey { }
        private class TestGrainGenericWithStringKey<T>: Grain, ITestGrainGenericWithStringKey<T> { }

        private static Dictionary<Type, Func<Type, Type, Type>> GrainTypeSwitch { get; } = new Dictionary<Type, Func<Type, Type, Type>>
        {
            [typeof(Guid)] = (grainType, stateType) =>
            {
                if(grainType == typeof(NotApplicable))
                {
                    return typeof(TestGrainWithGuidKey);
                }
                return typeof(TestGrainGenericWithGuidKey<double>);
            },
            [typeof(long)] = (grainType, stateType) =>
            {
                if(grainType == typeof(NotApplicable))
                {
                    return typeof(TestGrainWithIntegerKey);
                }
                return typeof(TestGrainGenericWithIntegerKey<double>);
            },
            [typeof(string)] = (grainType, stateType) =>
            {
                if(grainType == typeof(NotApplicable))
                {
                    return typeof(TestGrainWithStringKey);
                }
                return typeof(TestGrainGenericWithStringKey<double>);
            }
        };


        public static string GetGrainType<TGrainKey>()
        {
            return GetGrainType<TGrainKey, NotApplicable>();
        }


        /// <summary>
        /// Returns a grain type name.
        /// </summary>
        /// <typeparam name="TGrainKey">Used to choose the key type interface.</typeparam>
        /// <typeparam name="TGrain">Used to choose the type of grain.</typeparam>
        /// <returns>The class in typeof(T).AssemblyQualifiedName form.</returns>
        /// <remarks> ASSUMES Orleans give <em>grainType</em> parameters in this form to <see cref="Orleans.Storage.IStorageProvider"/> interface implementing functions.
        /// In <see cref="Orleans.Storage.AdoNetStorageProvider"/> private function <em>ExtractClassBaseType</em> relies this is in this form. Should be fixed
        /// if/when this is changed in Orleans.
        /// </remarks>
        public static string GetGrainType<TGrainKey, TGrain>()
        {
            Func<Type, Type, Type> func;
            if(GrainTypeSwitch.TryGetValue(typeof(TGrainKey), out func))
            {
                return (func(typeof(TGrainKey), typeof(TGrain))).AssemblyQualifiedName;
            }

            throw new ArgumentException(typeof(TGrainKey).Name);

        }
    }
}
