using System;
using System.Reflection;

namespace Orleans.Runtime
{
    [Serializable]
    internal class GenericGrainTypeData : GrainTypeData
    {
        private readonly Type activationType;
        private readonly Type stateObjectType;

        public GenericGrainTypeData(Type activationType, Type stateObjectType) :
            base(activationType, stateObjectType)
        {
            if (!activationType.GetTypeInfo().IsGenericTypeDefinition)
                throw new ArgumentException("Activation type is not generic: " + activationType.Name);

            this.activationType = activationType;
            this.stateObjectType = stateObjectType;
        }

        public GrainTypeData MakeGenericType(Type[] typeArgs)
        {
            // Need to make a non-generic instance of the class to access the static data field. The field itself is independent of the instantiated type.
            var concreteActivationType = activationType.MakeGenericType(typeArgs);
            var concreteStateObjectType = (stateObjectType != null && stateObjectType.GetTypeInfo().IsGenericType) ? stateObjectType.GetGenericTypeDefinition().MakeGenericType(typeArgs) : stateObjectType;

            return new GrainTypeData(concreteActivationType, concreteStateObjectType);
        }
    }
}
