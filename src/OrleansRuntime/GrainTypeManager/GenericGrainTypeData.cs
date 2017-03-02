using System;
using System.Reflection;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    [Serializable]
    internal class GenericGrainTypeData : GrainTypeData
    {
        private static readonly Type GenericGrainStateType = typeof(Grain<>);
        private readonly MultiClusterRegistrationStrategyManager registrationManager;
        private readonly Type activationType;
        private readonly Type stateObjectType;

        public GenericGrainTypeData(Type activationType, Type stateObjectType, MultiClusterRegistrationStrategyManager registrationManager) :
            base(activationType, stateObjectType, registrationManager)
        {
            if (!activationType.GetTypeInfo().IsGenericTypeDefinition)
                throw new ArgumentException("Activation type is not generic: " + activationType.Name);

            this.activationType = activationType;
            this.stateObjectType = stateObjectType;
            this.registrationManager = registrationManager;
        }

        public GrainTypeData MakeGenericType(Type[] typeArgs)
        {
            // Need to make a non-generic instance of the class to access the static data field. The field itself is independent of the instantiated type.
            var concreteActivationType = this.activationType.MakeGenericType(typeArgs);
            var typeInfo = this.stateObjectType?.GetTypeInfo();
            var concreteStateObjectType = typeInfo != null && (typeInfo.IsGenericType || typeInfo.IsGenericParameter)
                ? GetGrainStateType(concreteActivationType.GetTypeInfo())
                : this.stateObjectType;

            return new GrainTypeData(concreteActivationType, concreteStateObjectType, this.registrationManager);
        }

        private static Type GetGrainStateType(TypeInfo grainType)
        {
            while (true)
            {
                if (grainType == null) return null;
                if (grainType.IsGenericType && grainType.GetGenericTypeDefinition() == GenericGrainStateType)
                {
                    return grainType.GetGenericArguments()[0];
                }

                grainType = grainType.BaseType?.GetTypeInfo();
            }
        }
    }
}
