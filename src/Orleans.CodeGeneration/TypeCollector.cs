using System;
using System.Collections.Generic;

namespace Orleans.CodeGenerator
{
    internal class TypeCollector
    {
        public HashSet<Type> EncounteredTypes { get; } = new HashSet<Type>();

        public void RecordEncounteredType(Type type)
        {
            // Arrays, by-ref types, and pointers.
            if (type.HasElementType)
            {
                this.RecordEncounteredType(type.GetElementType());
                return;
            }
            
            if (type.IsConstructedGenericType)
            {
                this.RecordEncounteredType(type.GetGenericTypeDefinition());
                foreach (var typeParameter in type.GenericTypeArguments) this.RecordEncounteredType(typeParameter);
                return;
            }

            if (!this.EncounteredTypes.Add(type)) return;
            
            if (type.BaseType != null) this.RecordEncounteredType(type.BaseType);
            foreach (var interfaceType in type.GetInterfaces()) this.RecordEncounteredType(interfaceType);
        }
    }
}