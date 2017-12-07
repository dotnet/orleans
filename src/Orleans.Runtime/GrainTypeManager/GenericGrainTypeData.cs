using System;
using System.Reflection;

namespace Orleans.Runtime
{
    [Serializable]
    internal class GenericGrainTypeData : GrainTypeData
    {
        private readonly Type activationType;

        public GenericGrainTypeData(Type activationType) :
            base(activationType)
        {
            if (!activationType.GetTypeInfo().IsGenericTypeDefinition)
                throw new ArgumentException("Activation type is not generic: " + activationType.Name);

            this.activationType = activationType;
        }

        public GrainTypeData MakeGenericType(Type[] typeArgs)
        {
            // Need to make a non-generic instance of the class to access the static data field. The field itself is independent of the instantiated type.
            var concreteActivationType = this.activationType.MakeGenericType(typeArgs);
            return new GrainTypeData(concreteActivationType);
        }
    }
}
