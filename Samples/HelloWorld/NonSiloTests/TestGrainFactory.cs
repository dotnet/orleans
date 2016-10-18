using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Moq;
using Orleans;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;

namespace NonSiloTests
{
    public static class TestGrainFactory
    {
        private static readonly GrainCreator GrainCreator;

        private static readonly IStorageProvider StorageProvider;

        static TestGrainFactory()
        {
            var grainRuntime = Mock.Of<IGrainRuntime>();

            var mockStorageProvider = new Mock<IStorageProvider>();

            StorageProvider = mockStorageProvider.Object;

            GrainCreator = new GrainCreator(grainRuntime);
        }

        /// <summary>
        /// Creates a new instance of concrete grain type with a mocked runtime
        /// </summary>
        /// <typeparam name="T">Type of grain to create</typeparam>
        /// <param name="id">Id of the grain</param>
        /// <returns></returns>
        public static T CreateGrain<T>(long id) where T : Grain, IGrainWithIntegerKey
        {
            var identity = new Mock<IGrainIdentity>();
            identity.Setup(i => i.PrimaryKeyLong).Returns(id);

            return CreateGrain<T>(identity.Object);
        }

        private static T CreateGrain<T>(IGrainIdentity identity) where T : Grain
        {
            Grain grain;

            //Check to see if the grain is stateful
            if (IsSubclassOfRawGeneric(typeof(Grain<>), typeof(T)))
            {
                var grainGenericArgs = typeof(T).BaseType?.GenericTypeArguments;

                if (grainGenericArgs == null || grainGenericArgs.Length == 0)
                    throw new Exception($"Unable to get grain state type info for {typeof(T)}");

                //Get the state type
                var stateType = grainGenericArgs[0];

                //Create a new stateful grain
                grain = GrainCreator.CreateGrainInstance(typeof(T), identity, stateType, StorageProvider);
            }
            else
            {
                //Create a stateless grain
                grain = GrainCreator.CreateGrainInstance(typeof(T), identity) as T;
            }

            if (grain == null)
                return null;

            //Emulate the grain's lifecycle
            grain.OnActivateAsync();

            return grain as T;
        }

        private static bool IsSubclassOfRawGeneric(Type generic, Type toCheck)
        {
            while (toCheck != null && toCheck != typeof(object))
            {
                var cur = toCheck.IsGenericType ? toCheck.GetGenericTypeDefinition() : toCheck;
                if (generic == cur)
                {
                    return true;
                }
                toCheck = toCheck.BaseType;
            }
            return false;
        }
    }
}
