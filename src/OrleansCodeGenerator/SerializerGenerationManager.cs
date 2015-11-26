/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading.Tasks;

    using Orleans.Runtime;
    using Orleans.Serialization;

    using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;

    /// <summary>
    /// The serializer generation manager.
    /// </summary>
    internal static class SerializerGenerationManager
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private static readonly TraceLogger Log;

        /// <summary>
        /// The types to process.
        /// </summary>
        private static readonly HashSet<Type> TypesToProcess;

        /// <summary>
        /// The processed types.
        /// </summary>
        private static readonly HashSet<Type> ProcessedTypes;

        /// <summary>
        /// Initializes static members of the <see cref="SerializerGenerationManager"/> class.
        /// </summary>
        static SerializerGenerationManager()
        {
            TypesToProcess = new HashSet<Type>();
            ProcessedTypes = new HashSet<Type>();
            Log = TraceLogger.GetLogger(typeof(SerializerGenerationManager).Name);
        }
        
        internal static bool RecordTypeToGenerate(Type t, Module module, Assembly targetAssembly)
        {
            if (TypeUtilities.IsTypeIsInaccessibleForSerialization(t, module, targetAssembly))
            {
                return false;
            }

            var typeInfo = t.GetTypeInfo();

            if (typeInfo.IsGenericParameter || ProcessedTypes.Contains(t) || TypesToProcess.Contains(t)
                || typeof(Exception).GetTypeInfo().IsAssignableFrom(t)) return false;

            if (typeInfo.IsArray)
            {
                RecordTypeToGenerate(typeInfo.GetElementType(), module, targetAssembly);
                return false;
            }

            if (typeInfo.IsNestedPublic || typeInfo.IsNestedFamily || typeInfo.IsNestedPrivate)
            {
                Log.Warn(
                    ErrorCode.CodeGenIgnoringTypes,
                    "Skipping serializer generation for nested type {0}. If this type is used frequently, you may wish to consider making it non-nested.",
                    t.Name);
            }

            if (typeInfo.IsGenericType)
            {
                var args = t.GetGenericArguments();
                foreach (var arg in args)
                {
                    RecordTypeToGenerate(arg, module, targetAssembly);
                }
            }

            if (typeInfo.IsInterface || typeInfo.IsAbstract || t == typeof (object) || t == typeof (void)
                || GrainInterfaceData.IsTaskType(t)) return false;

            if (typeInfo.IsGenericType)
            {
                var def = typeInfo.GetGenericTypeDefinition();
                if (def == typeof (Task<>) || (SerializationManager.GetSerializer(def) != null) ||
                    ProcessedTypes.Contains(def) || typeof(IAddressable).IsAssignableFrom(def)) return false;

                if (def.Namespace != null && (def.Namespace.Equals("System") || def.Namespace.StartsWith("System.")))
                    Log.Warn(
                        ErrorCode.CodeGenSystemTypeRequiresSerializer,
                        "System type " + def.Name + " requires a serializer.");
                else
                    TypesToProcess.Add(def);

                return false;
            }

            if (typeInfo.IsOrleansPrimitive() || (SerializationManager.GetSerializer(t) != null) ||
                typeof(IAddressable).GetTypeInfo().IsAssignableFrom(t)) return false;

            if (typeInfo.Namespace != null && (typeInfo.Namespace.Equals("System") || typeInfo.Namespace.StartsWith("System.")))
            {
                var message = "System type " + t.Name + " may require a custom serializer for optimal performance. "
                              + "If you use arguments of this type a lot, consider asking the Orleans team to build a custom serializer for it.";
                Log.Warn(ErrorCode.CodeGenSystemTypeRequiresSerializer, message);
                return false;
            }

            if (TypeUtils.HasAllSerializationMethods(t)) return false;
            
            TypesToProcess.Add(t);
            return true;
        }

        internal static bool GetNextTypeToProcess(out Type next)
        {
            next = null;
            if (TypesToProcess.Count == 0) return false;

            var enumerator = TypesToProcess.GetEnumerator();
            enumerator.MoveNext();
            next = enumerator.Current;

            TypesToProcess.Remove(next);
            ProcessedTypes.Add(next);

            return true;
        }
    }
}