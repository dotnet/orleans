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

﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Orleans.Serialization;
using Orleans.Runtime;

namespace Orleans.CodeGeneration.Serialization
{
    internal static class SerializerGenerationManager
    {
        private static HashSet<Type> typesToProcess;
        private static HashSet<Type> processedTypes;

        internal static void Init()
        {
            ConsoleText.WriteStatus("Initializing serializer generation manager");
            typesToProcess = new HashSet<Type>();
            processedTypes = new HashSet<Type>();
        }

        internal static void RecordTypeToGenerate(Type t)
        {
            if (t.IsGenericParameter || processedTypes.Contains(t) || typesToProcess.Contains(t) 
                ||typeof (Exception).IsAssignableFrom(t)) return;

            if (t.IsArray)
            {
                RecordTypeToGenerate(t.GetElementType());
                return;
            }

            if (t.IsNestedPublic || t.IsNestedFamily || t.IsNestedPrivate)
                Console.WriteLine("Skipping serializer generation for nested type {0}. If this type is used frequently, you may wish to consider making it non-nested.", t.Name);

            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                foreach (var arg in args)
                    if (!arg.IsGenericParameter)
                        RecordTypeToGenerate(arg);
            }

            if (t.IsInterface || t.IsAbstract || t.IsEnum || t == typeof (object) || t == typeof (void) 
                || GrainInterfaceData.IsTaskType(t)) return;

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof (Task<>) || (SerializationManager.GetSerializer(def) != null) ||
                    processedTypes.Contains(def) || typeof (IAddressable).IsAssignableFrom(def)) return;

                if (def.Namespace.Equals("System") || def.Namespace.StartsWith("System."))
                    ConsoleText.WriteError("System type " + def.Name + " requires a serializer.");
                else
                    typesToProcess.Add(def);

                return;
            }

            if (t.IsOrleansPrimitive() || (SerializationManager.GetSerializer(t) != null) ||
                typeof (IAddressable).IsAssignableFrom(t)) return;

            if (t.Namespace.Equals("System") || t.Namespace.StartsWith("System."))
            {
                ConsoleText.WriteError("System type " + t.Name + " may require a custom serializer for optimal performance.");
                ConsoleText.WriteError("If you use arguments of this type a lot, consider asking the Orleans team to build a custom serializer for it.");
                return;
            }

            if (t.IsArray)
            {
                RecordTypeToGenerate(t.GetElementType());
                return;
            }

            bool hasCopier = false;
            bool hasSerializer = false;
            bool hasDeserializer = false;
            foreach (var method in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (method.GetCustomAttributes(typeof(SerializerMethodAttribute), false).Length > 0)
                {
                    hasSerializer = true;
                }
                else if (method.GetCustomAttributes(typeof(DeserializerMethodAttribute), false).Length > 0)
                {
                    hasDeserializer = true;
                }

                if (method.GetCustomAttributes(typeof (CopierMethodAttribute), false).Length > 0)
                    hasCopier = true;
            }
            if (hasCopier && hasSerializer && hasDeserializer) return;

            typesToProcess.Add(t);
        }

        internal static bool GetNextTypeToProcess(out Type next)
        {
            next = null;
            if (typesToProcess.Count == 0) return false;

            var enumerator = typesToProcess.GetEnumerator();
            enumerator.MoveNext();
            next = enumerator.Current;

            typesToProcess.Remove(next);
            processedTypes.Add(next);

            return true;
        }

        internal static void GenerateSerializers(Assembly grainAssembly, Dictionary<string, NamespaceGenerator> namespaceDictionary, string outputAssemblyName, Language language)
        {
            Type toGen;
            NamespaceGenerator extraNamespace = null;
            ConsoleText.WriteStatus("ClientGenerator - Generating serializer classes");
            while (GetNextTypeToProcess(out toGen))
            {
                ConsoleText.WriteStatus("\ttype " + toGen.FullName + " in namespace " + toGen.Namespace);
                NamespaceGenerator typeNamespace;

                if (!namespaceDictionary.TryGetValue(toGen.Namespace, out typeNamespace))
                {
                    if (extraNamespace == null)
                    {
                        // Calculate a unique namespace name based on the output assembly name
                        extraNamespace = new NamespaceGenerator(grainAssembly, outputAssemblyName + "Serializers", language);
                        namespaceDictionary.Add("OrleansSerializers", extraNamespace);
                    }

                    typeNamespace = extraNamespace;
                    typeNamespace.RecordReferencedAssembly(toGen);
                    foreach (var info in toGen.GetFields()) { typeNamespace.RecordReferencedNamespaceAndAssembly(info.FieldType); }
                    foreach (var info in toGen.GetProperties()) { typeNamespace.RecordReferencedNamespaceAndAssembly(info.PropertyType); }
                    foreach (var info in toGen.GetMethods())
                    {
                        typeNamespace.RecordReferencedNamespaceAndAssembly(info.ReturnType);
                        foreach (var arg in info.GetParameters()) { typeNamespace.RecordReferencedNamespaceAndAssembly(arg.ParameterType); }
                    }
                }

                SerializationGenerator.GenerateSerializationForClass(toGen, typeNamespace.ReferencedNamespace, typeNamespace.ReferencedNamespaces, language);
            }
        }
    }
}