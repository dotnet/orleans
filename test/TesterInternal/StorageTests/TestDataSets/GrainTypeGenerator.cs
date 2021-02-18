using Orleans;
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

        public interface ITestGrainWithIntegerKey: IGrainWithIntegerKey { }

        public interface ITestGrainGenericWithIntegerKey<T>: IGrainWithIntegerKey { }

        public class TestGrainWithIntegerKey: Grain, ITestGrainWithIntegerKey { }

        public class TestGrainGenericWithIntegerKey<T>: Grain, ITestGrainGenericWithIntegerKey<T> { }

        public interface ITestGrainWithGuidKey: IGrainWithGuidKey { }

        public interface ITestGrainGenericWithGuidKey<T>: IGrainWithGuidKey { }

        public class TestGrainWithGuidKey: Grain, ITestGrainWithGuidKey { }

        public class TestGrainGenericWithGuidKey<T>: Grain, ITestGrainGenericWithGuidKey<T> { }

        public interface ITestGrainWithStringKey: IGrainWithStringKey { }

        public interface ITestGrainGenericWithStringKey<T>: IGrainWithStringKey { }

        public class TestGrainWithStringKey: Grain, ITestGrainWithStringKey { }

        public class TestGrainGenericWithStringKey<T>: Grain, ITestGrainGenericWithStringKey<T> { }

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

// Orleans.Storage.AdoNetStorageProvider cannot be resolved, because the containing assembly is not referenced since not needed.
#pragma warning disable 1574
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
#pragma warning restore 1574
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

#pragma warning restore ORLEANS0102
