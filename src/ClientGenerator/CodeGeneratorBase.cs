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

using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using Microsoft.VisualBasic;

using Orleans.Runtime;

namespace Orleans.CodeGeneration
{

    /// <summary>
    /// Base class for code generators. It contains a number of helper methods for code generation
    /// </summary>
    internal abstract class CodeGeneratorBase : MarshalByRefObject
    {
        /// <summary>
        /// Stores the compile errors
        /// </summary>
        protected static List<string> Errors = new List<string>();
        protected const string CODE_GENERATOR_NAME = "Orleans-CodeGenerator";
        protected static readonly string CodeGeneratorVersion = RuntimeVersion.FileVersion;
        protected string CurrentNamespace;
        private readonly Language language;

        /// <summary>
        /// Returns a name string for a nested class type name (ClassName.TypeName)
        /// for a serializable type, the name string is only the TypeName
        /// </summary>
        internal static string GetNestedClassName(string name, bool noNamespace)
        {
            var builder = new StringBuilder();
            int index = 0;
            int start = 0;
            while (start < name.Length)
            {
                index = name.IndexOf('+', start);
                if (index == -1) break;

                builder.Append(name.Substring(start, index - start));
                builder.Append('.');
                start = index + 1;
            }
            if (index == -1)
            {
                if (noNamespace)
                    return name.Substring(start);

                builder.Append(name.Substring(start));
            }

            return builder.ToString();
        }

        protected CodeGeneratorBase(Language language)
        {
            ReferencedNamespaces = new HashSet<string>();
            ReferencedAssemblies = new HashSet<string>();
            this.language = language;
        }

        internal HashSet<string> ReferencedNamespaces { get; private set; }
        protected HashSet<string> ReferencedAssemblies { get; private set; }

        /// <summary>
        /// Calls the appropriate GetInterfaceInfo method depending on whether we are dealing with an implicit or explicit service type and
        /// returns the a dictionary of Inteface and Event info exposed by either service type
        /// </summary>
        /// <param name="grainType"></param>
        /// <returns></returns>
        internal static GrainInterfaceInfo GetInterfaceInfo(Type grainType)
        {
            var result = new GrainInterfaceInfo();
            Dictionary<int, Type> interfaces = GrainInterfaceData.GetRemoteInterfaces(grainType);
            if (interfaces.Keys.Count == 0)
            {
                // Should never happen!
                Debug.Fail("Could not find any service interfaces for type=" + grainType.Name);
            }

            IEqualityComparer<InterfaceInfo> ifaceComparer = new InterfaceInfoComparer();
            foreach (var interfaceId in interfaces.Keys)
            {
                Type interfaceType = interfaces[interfaceId];
                var interfaceInfo = new InterfaceInfo(interfaceType);

                if (!result.Interfaces.Values.Contains(interfaceInfo, ifaceComparer))
                    result.Interfaces.Add(GrainInterfaceData.GetGrainInterfaceId(interfaceType), interfaceInfo);
            }

            return result;
        }
        
        /// <summary>
        /// Decide whether this method is a remote grain call method
        /// </summary>
        internal protected static bool IsGrainMethod(MethodInfo methodInfo)
        {
            if (methodInfo == null) throw new ArgumentNullException("methodInfo", "Cannot inspect null method info");

            // ignore static, event, or non-remote methods
            if (methodInfo.IsStatic || methodInfo.IsSpecialName || IsSpecialEventMethod(methodInfo))
                return false; // Methods which are derived from base class or object class, or property getter/setter methods

            return methodInfo.DeclaringType.IsInterface && typeof(IAddressable).IsAssignableFrom(methodInfo.DeclaringType);
        }
        
        internal static CodeDomProvider GetCodeProvider(Language language, bool debug = false)
        {
            switch (language)
            {
                case Language.CSharp:
                {
                    var providerOptions = new Dictionary<string, string> { { "CompilerVersion", "v4.0" } };
                    if (debug)
                        providerOptions.Add("debug", "full");

                    return new CSharpCodeProvider(providerOptions);
                }
                case Language.VisualBasic:
                {
                    var providerOptions = new Dictionary<string, string>();
                    if (debug)
                        providerOptions.Add("debug", "full");

                    var prov = new VBCodeProvider(providerOptions);
                    return prov;
                }
                default:
                    return null;
            }
        }

        internal static void MarkAsGeneratedCode(CodeTypeDeclaration classRef, bool suppressDebugger = false, bool suppressCoverage = true)
        {
            classRef.CustomAttributes.Add(new CodeAttributeDeclaration(
                    new CodeTypeReference(typeof(GeneratedCodeAttribute)),
                    new CodeAttributeArgument(new CodePrimitiveExpression(CODE_GENERATOR_NAME)),
                    new CodeAttributeArgument(new CodePrimitiveExpression(CodeGeneratorVersion))));

            if (classRef.IsInterface) return;

            if (suppressCoverage) classRef.CustomAttributes.Add(
                new CodeAttributeDeclaration(new CodeTypeReference(typeof(System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute))));
            if (suppressDebugger) classRef.CustomAttributes.Add(
                new CodeAttributeDeclaration(new CodeTypeReference(typeof(DebuggerNonUserCodeAttribute))));
        }
        
        /// <summary>
        /// Finds the Persistent interface given the grain class
        /// </summary>
        /// <param name="sourceType">source grain type</param>
        /// <returns>Persistent interface </returns>
        protected static Type GetPersistentInterface(Type sourceType)
        {
            if (!typeof (Grain).IsAssignableFrom(sourceType)) return null;

            Type persistentInterface = null;
            Type baseType = sourceType.BaseType;

            if (baseType == null || baseType == typeof (Grain)) return null;

            // go up till we find the base classe that derives directly from Grain
            while (!(baseType.BaseType == typeof (Grain)))
                baseType = baseType.BaseType;

            // Now we have a base class that derives from Grain,
            // make sure it is generic and actually the Grain<T>
            if (baseType.IsGenericType && baseType.Name.StartsWith(typeof(Grain).Name) 
                && baseType.Namespace == typeof(Grain).Namespace)
            {
                // the argument is type of peristent interface
                persistentInterface = baseType.GetGenericArguments()[0];
            }
            return persistentInterface;
        }protected virtual string GetInvokerImpl(GrainInterfaceData si, CodeTypeDeclaration invokerClass, Type grainType, GrainInterfaceInfo grainInterfaceInfo, bool isClient)
        {
            throw new NotImplementedException("InvokerGeneratorBasics.GetInvokerImpl");
        }

        /// <summary>
        /// get the name string for a nested class type name
        /// </summary>
        protected static string GetNestedClassName(string name)
        {
            var builder = new StringBuilder();
            int index = 0;
            int start = 0;

            while (start < name.Length)
            {
                index = name.IndexOf('+', start);
                if (index == -1) break;

                builder.Append(name.Substring(start, index - start));
                builder.Append('.');
                start = index + 1;
            }
            if (index == -1)
                builder.Append(name.Substring(start));

            return builder.ToString();
        }

        /// <summary>
        /// Decide whether the method is some special methods that implement an event. 
        /// Special Methods, like add_** and remove_**, shall be marked SpecialName in the metadata 
        /// </summary>
        protected static bool IsSpecialEventMethod(MethodInfo methodInfo)
        {
            return methodInfo.IsSpecialName &&
                (!(methodInfo.Name.StartsWith("get_") || methodInfo.Name.StartsWith("set_")));
        }

        protected virtual string GetOrleansGetMethodNameImpl(Type grainType, GrainInterfaceInfo grainInterfaceInfo)
        {
            throw new NotImplementedException("InvokerGeneratorBasics.GetOrleansGetMethodNameImpl");
        }

        /// <summary>
        /// Get the name string for generic type
        /// </summary>
        protected virtual string GetGenericTypeName(Type type, Action<Type> referred, Func<Type, bool> noNamespace = null)
        {
            throw new NotImplementedException("GetGenericTypeName");
        }

        /// <summary>
        /// Get the name string for generic type
        /// </summary>
        protected virtual string GetGenericTypeName(Type type)
        {
            // Add in the namespace of the type and the assembly file in which the type is defined
            AddReferencedAssembly(type);
            // Add in the namespace of the type and the assembly file in which any generic argument types are defined
            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments())
                    AddReferencedAssembly(argument);
            }

            var typeName = TypeUtils.GetTemplatedName(type, t => CurrentNamespace != t.Namespace && !ReferencedNamespaces.Contains(t.Namespace), language);
            return GetNestedClassName(typeName);
        }

        /// <summary>
        /// Returns the language-dependent name for the provided <paramref name="parameter"/>.
        /// </summary>
        /// <param name="parameter">The parameter.</param>
        /// <returns>The language-dependent name for the provided <paramref name="parameter"/>.</returns>
        protected virtual string GetParameterName(ParameterInfo parameter)
        {
            throw new NotImplementedException("GetParamterName");
        }

        protected void AddReferencedAssembly(Type t)
        {
            var assembly = t.Assembly.GetName().Name + Path.GetExtension(t.Assembly.Location).ToLowerInvariant();
            if (!ReferencedAssemblies.Contains(assembly))
                ReferencedAssemblies.Add(assembly);
        }
        

        internal class InterfaceInfo
        {
            public Type InterfaceType { get; private set; }
            public Dictionary<int, MethodInfo> Methods { get; private set; }

            public InterfaceInfo(Type interfaceType)
            {
                InterfaceType = interfaceType;
                Methods = GetGrainMethods();
            }

            private Dictionary<int, MethodInfo> GetGrainMethods()
            {
                var grainMethods = new Dictionary<int, MethodInfo>();
                foreach (var interfaceMethodInfo in GrainInterfaceData.GetMethods(InterfaceType))
                {
                    ParameterInfo[] parameters = interfaceMethodInfo.GetParameters();
                    var args = new Type[parameters.Length];

                    for (int i = 0; i < parameters.Length; i++)
                        args[i] = parameters[i].ParameterType;

                    MethodInfo methodInfo = InterfaceType.GetMethod(interfaceMethodInfo.Name, args) ?? interfaceMethodInfo;

                    if (IsGrainMethod(methodInfo))
                        grainMethods.Add(GrainInterfaceData.ComputeMethodId(methodInfo), methodInfo);
                }
                return grainMethods;
            }

            public override string ToString()
            {
                return "InterfaceInfo:" + InterfaceType.FullName + ",#Methods=" + Methods.Count;
            }
        }

        internal class GrainInterfaceInfo
        {
            public Dictionary<int, InterfaceInfo> Interfaces { get; private set; }

            public GrainInterfaceInfo()
            {
                Interfaces = new Dictionary<int, InterfaceInfo>();
            }
        }

        internal class InterfaceInfoComparer : IEqualityComparer<InterfaceInfo>
        {

            #region IEqualityComparer<InterfaceInfo> Members

            public bool Equals(InterfaceInfo x, InterfaceInfo y)
            {
                var xFullName = TypeUtils.GetFullName(x.InterfaceType);
                var yFullName = TypeUtils.GetFullName(y.InterfaceType);
                return String.CompareOrdinal(xFullName, yFullName) == 0;
            }

            public int GetHashCode(InterfaceInfo obj)
            {
                throw new NotImplementedException();
            }

            #endregion
        }
    }
}
