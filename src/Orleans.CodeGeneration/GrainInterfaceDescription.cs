using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator
{
    internal class GrainInterfaceDescription
    {
        public TypeSyntax Interface { get; set; }
        public TypeSyntax Reference { get; set; }
        public TypeSyntax Invoker { get; set; }
        public int InterfaceId { get; set; }
    }

    internal class GrainClassDescription
    {
        public TypeSyntax ClassType { get; set; }
    }

    internal class SerializationTypeDescriptions
    {
        public List<SerializerTypeDescription> SerializerTypes { get; } = new List<SerializerTypeDescription>();
        public List<KnownTypeDescription> KnownTypes { get; } = new List<KnownTypeDescription>();
    }

    internal class SerializerTypeDescription
    {
        public TypeSyntax Serializer { get; set; }
        public TypeSyntax Target { get; set; }
    }

    public class KnownTypeDescription
    {
        public string Type { get; set; }
        public string TypeKey { get; set; }
    }
}