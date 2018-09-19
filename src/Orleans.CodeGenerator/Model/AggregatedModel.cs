using System.Collections.Generic;

namespace Orleans.CodeGenerator.Model
{
    internal class AggregatedModel
    {
        public List<GrainClassDescription> GrainClasses { get; } = new List<GrainClassDescription>();
        public List<GrainInterfaceDescription> GrainInterfaces { get; } = new List<GrainInterfaceDescription>();
        public SerializationTypeDescriptions Serializers { get; } = new SerializationTypeDescriptions();
    }
}