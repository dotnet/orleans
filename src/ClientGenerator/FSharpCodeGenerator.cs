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
using System.CodeDom;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Linq;

using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Visual Basic code generator. It contains the VB-specific logic for code generation of 
    /// state classes, factories, grain reference classes, and method invokers. 
    /// </summary>
    internal class FSharpCodeGenerator : NamespaceGenerator
    {
        int indentLevel;
        void IncreaseIndent() { indentLevel += 4; }
        void DecreaseIndent() { indentLevel -= 4; }

        void StartNewLine()
        {
            generatedCode.Append(Environment.NewLine);
            generatedCode.Append(' ', indentLevel);
        }

        readonly StringBuilder generatedCode = new StringBuilder();

        public FSharpCodeGenerator(Assembly grainAssembly, string nameSpace)
            : base(grainAssembly, nameSpace, Language.FSharp)
        {
            indentLevel = 0;
            generatedCode.AppendFormat(@"namespace {0}", nameSpace);
            StartNewLine();
        }

        public void Output(System.IO.StreamWriter stream)
        {
            stream.Write(generatedCode.ToString());
        }

        protected override string GetGenericTypeName(Type type, Action<Type> referred, Func<Type, bool> noNamespace = null)
        {
            if (type == null) throw new ArgumentNullException("type");
            if (noNamespace == null)
                noNamespace = t => true;

            referred(type);
            var name = (noNamespace(type) && !type.IsNested) ? type.Name : TypeUtils.GetFullName(type, Language.FSharp);

            if (!type.IsGenericType)
            {
                if (type.FullName == null) return type.Name;

                var result = GetNestedClassName(name);
                return result == "Void" ? "unit" : result;
            }

            var builder = new StringBuilder();
            int index = name.IndexOf("`", StringComparison.Ordinal);
            builder.Append(GetNestedClassName(name.Substring(0, index), noNamespace(type)));
            builder.Append('<');
            bool isFirstArgument = true;

            foreach (Type argumentType in type.GetGenericArguments())
            {
                if (!isFirstArgument)
                    builder.Append(',');
                if (argumentType.IsGenericParameter)
                    builder.Append('\'');
                builder.Append(GetGenericTypeName(argumentType, referred, noNamespace));
                isFirstArgument = false;
            }
            builder.Append('>');
            return builder.ToString();
        }


        #region Grain State Classes

        protected override CodeTypeDeclaration GetStateClass(GrainInterfaceData grainInterfaceData, Action<Type> referred, string stateClassBaseName, string stateClassName, out bool hasStateClass)
        {
            var sourceType = grainInterfaceData.Type;
            stateClassName = FixupTypeName(stateClassName);
            CodeTypeParameterCollection genericTypeParams = grainInterfaceData.GenericTypeParams;
            Func<Type, bool> nonamespace = t => false;
            Type persistentInterface = GetPersistentInterface(sourceType);
            
            Dictionary<string, PropertyInfo> asyncProperties = GrainInterfaceData.GetPersistentProperties(persistentInterface)
                .ToDictionary(p => p.Name.Substring(p.Name.LastIndexOf('.') + 1), p => p);

            Dictionary<string, string> properties = asyncProperties.ToDictionary(p => p.Key,
                    p => GetGenericTypeName(GrainInterfaceData.GetPromptType(p.Value.PropertyType), referred, nonamespace));

            hasStateClass = properties.Count > 0;

            if (!hasStateClass) return null;

            var typeAccess = (persistentInterface != null && !persistentInterface.IsPublic) ? "internal" : "public";

            StartNewLine();
            StartNewLine();
            generatedCode.AppendFormat(@"[<System.CodeDom.Compiler.GeneratedCodeAttribute(""{0}"", ""{1}"")>]", CODE_GENERATOR_NAME, CodeGeneratorVersion);
            StartNewLine();
            generatedCode.AppendFormat(@"[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute()>]");
            StartNewLine();
            generatedCode.AppendFormat(@"[<System.SerializableAttribute()>]");
            StartNewLine();
            var grainName = grainInterfaceData.Type.Namespace + "." + TypeUtils.GetParameterizedTemplateName(grainInterfaceData.Type);
            generatedCode.AppendFormat(@"[<global.Orleans.CodeGeneration.GrainStateAttribute(""{0}"")>]", grainName);
            StartNewLine();
            generatedCode.AppendFormat(@"type {0} {1}", typeAccess, stateClassBaseName);

            if (genericTypeParams != null && genericTypeParams.Count > 0)
            {
                generatedCode.Append('<');
                for (int p = 0; p < genericTypeParams.Count; ++p)
                {
                    if (p > 0) 
                        generatedCode.Append(',');

                    CodeTypeParameter param = genericTypeParams[p];
                    generatedCode.Append('\'').Append(param.Name);
                }
                generatedCode.Append('>');
            }

            generatedCode.AppendFormat(@"() =");
            IncreaseIndent();
            StartNewLine();
            generatedCode.AppendFormat(@"inherit global.Orleans.CodeGeneration.GrainState(""{0}"")", grainName);
            StartNewLine();

            var declaringTypes = new Dictionary<string, List<KeyValuePair<string, PropertyInfo>>>();

            foreach (var pair in asyncProperties)
            {
                var dtName = GetGenericTypeName(pair.Value.DeclaringType, referred, nonamespace);
                if (!declaringTypes.ContainsKey(dtName))
                    declaringTypes.Add(dtName, new List<KeyValuePair<string, PropertyInfo>>());

                var lst = declaringTypes[dtName];
                lst.Add(pair);
            }

            foreach (var declaringType in declaringTypes)
            {
                StartNewLine();
                generatedCode.AppendFormat(@"interface {0} with", declaringType.Key);

                IncreaseIndent();
                foreach (var pair in declaringType.Value)
                {
                    var propertyType = pair.Value.PropertyType;

                    bool noCreateNew = propertyType.IsPrimitive || typeof(string).IsAssignableFrom(propertyType) // Primative types
                        || propertyType.IsAbstract || propertyType.IsInterface || propertyType.IsGenericParameter // No concrete implementation
                        || propertyType.GetConstructor(Type.EmptyTypes) == null; // No default constructor

                    var initExpr = noCreateNew ?
                        string.Format("Unchecked.defaultof<{0}>", GetGenericTypeName(propertyType, referred, nonamespace)) : 
                        string.Format("{0}()", GetGenericTypeName(propertyType, referred, nonamespace));

                    StartNewLine();
                    generatedCode.AppendFormat(@"override val {0} = {1} with get,set", pair.Key, initExpr);
                }
                DecreaseIndent();
            }

            GenerateSetAll(asyncProperties, referred);
            GenerateAsDictionary(asyncProperties, referred);
            GenerateToString(stateClassName, asyncProperties, referred);

            // Generate the serialization members.

            StartNewLine();
            StartNewLine();
            generatedCode.AppendFormat(@"[<global.Orleans.CodeGeneration.CopierMethodAttribute()>]");
            StartNewLine();
            generatedCode.AppendFormat(@"static member public _Copier(original:obj) : obj =");
            IncreaseIndent();
            StartNewLine();
            generatedCode.AppendFormat(@"let input = original :?> {0}", stateClassBaseName);
            StartNewLine();
            generatedCode.AppendFormat(@"input.DeepCopy() :> obj");
            DecreaseIndent();

            StartNewLine();
            StartNewLine();
            generatedCode.AppendFormat(@"[<global.Orleans.CodeGeneration.SerializerMethodAttribute()>]");
            StartNewLine();
            generatedCode.AppendFormat(@"static member public _Serializer(original:obj, stream:global.Orleans.Serialization.BinaryTokenStreamWriter, expected:System.Type) : unit =");
            IncreaseIndent();
            StartNewLine();
            generatedCode.AppendFormat(@"let input = original :?> {0}", stateClassBaseName);
            StartNewLine();
            generatedCode.AppendFormat(@"input.SerializeTo(stream)");
            DecreaseIndent();

            StartNewLine();
            StartNewLine();
            generatedCode.AppendFormat(@"[<global.Orleans.CodeGeneration.DeserializerMethodAttribute()>]");
            StartNewLine();
            generatedCode.AppendFormat(@"static member public _Deserializer(expected:System.Type, stream:global.Orleans.Serialization.BinaryTokenStreamReader) : obj =");
            IncreaseIndent();
            StartNewLine();
            generatedCode.AppendFormat(@"let result = {0}()", stateClassBaseName);
            StartNewLine();
            generatedCode.AppendFormat(@"result.DeserializeFrom(stream)");
            StartNewLine();
            generatedCode.AppendFormat(@"result :> obj");
            DecreaseIndent();

            DecreaseIndent();

            return null;
        }

        protected override void GenerateStateClassProperty(CodeTypeDeclaration stateClass, PropertyInfo propInfo, string name, string type)
        {
            throw new NotImplementedException("GenerateStateClassProperty");
        }

        private void GenerateToString(string stateClassName, Dictionary<string, PropertyInfo> properties, Action<Type> referred)
        {
            Func<Type, bool> nonamespace = t => false;

            int i = 0;
            StartNewLine();
            StartNewLine();
            generatedCode.AppendFormat(@"override this.ToString() : string = ");
            IncreaseIndent();
            StartNewLine();
            generatedCode.AppendFormat(@"System.String.Format(""{0}( {1})""{2})", 
                stateClassName,
                properties.Keys.ToStrings(p => p + "={" + i++ + "} ", ""),
                properties.Values.ToStrings(p => string.Format("(this :> {0}).{1}", 
                    GetGenericTypeName(p.DeclaringType, referred, nonamespace), p.Name), ", "));
            DecreaseIndent();
        }

        private void GenerateSetAll(Dictionary<string, PropertyInfo> properties, Action<Type> referred)
        {
            Func<Type, bool> nonamespace = t => false;

            StartNewLine();
            StartNewLine();
            generatedCode.AppendFormat(@"override this.SetAll(values : System.Collections.Generic.IDictionary<string,obj>) =");
            
            IncreaseIndent();
            StartNewLine();
            generatedCode.AppendFormat(@"if values <> null then");
            
            IncreaseIndent();
            foreach (var pair in properties)
            {
                var dtName = GetGenericTypeName(pair.Value.DeclaringType, referred, nonamespace);

                StartNewLine();
                generatedCode.AppendFormat("match values.TryGetValue(\"{0}\") with", pair.Key);
                StartNewLine();
                if ("long".Equals(pair.Value.PropertyType.Name, StringComparison.OrdinalIgnoreCase)
                    || "int64".Equals(pair.Value.PropertyType.Name, StringComparison.OrdinalIgnoreCase))
                {
                    generatedCode.AppendFormat("| (true, x) -> (this :> {2}).{0} <- x :?> {1}", pair.Key, pair.Value.PropertyType.FullName, dtName);
                }
                else if ("int".Equals(pair.Value.PropertyType.Name, StringComparison.OrdinalIgnoreCase)
                    || "int32".Equals(pair.Value.PropertyType.Name, StringComparison.OrdinalIgnoreCase))
                {
                    generatedCode.AppendFormat("| (true, x) -> (this :> {2}).{0} <- x :?> {1}", pair.Key, pair.Value.PropertyType.FullName, dtName);
                }
                else
                {
                    generatedCode.AppendFormat("| (true, x) -> (this :> {2}).{0} <- x :?> {1}", pair.Key, pair.Value.PropertyType.FullName, dtName);
                }

                StartNewLine();
                generatedCode.AppendFormat("| (false,_) -> ()");
            }            
            DecreaseIndent();          
            DecreaseIndent();
        }

        private void GenerateAsDictionary(Dictionary<string, PropertyInfo> properties, Action<Type> referred)
        {
            Func<Type, bool> nonamespace = t => false;

            StartNewLine();
            StartNewLine();
            generatedCode.AppendFormat(@"override this.AsDictionary() : System.Collections.Generic.IDictionary<string,obj> =");

            IncreaseIndent();
            StartNewLine();
            generatedCode.AppendFormat(@"let result = System.Collections.Generic.Dictionary<string,obj>()");

            foreach (var pair in properties)
            {
                var genericTypeName = GetGenericTypeName(pair.Value.DeclaringType, referred, nonamespace);

                StartNewLine();
                generatedCode.AppendFormat("result.[\"{0}\"] <- (this :> {1}).{0}", pair.Key, genericTypeName);
            }

            StartNewLine();
            generatedCode.AppendFormat("result");
            
            DecreaseIndent();
        }
        #endregion

        #region Grain Interfaces
        protected override void AddCreateObjectReferenceMethods(GrainInterfaceData grainInterfaceData, CodeTypeDeclaration factoryClass)
        {
            throw new NotImplementedException("AddCreateObjectReferenceMethods");
        }

        protected override string GetOrleansGetMethodNameImpl(Type grainType, GrainInterfaceInfo grainInterfaceInfo)
        {
            throw new NotImplementedException("GetOrleansGetMethodNameImpl");
        }
        protected override string GetInvokerImpl(GrainInterfaceData si, CodeTypeDeclaration invokerClass, Type grainType, GrainInterfaceInfo grainInterfaceInfo, bool isClient)
        {
            throw new NotImplementedException("GetInvokerImpl");
        }
        protected override void AddGetGrainMethods(GrainInterfaceData iface, CodeTypeDeclaration factoryClass)
        {
            throw new NotImplementedException("AddGetGrainMethods");
        }
        
        /// <summary>
        /// Generate Cast method in CodeDom and add it in reference class
        /// </summary>
        /// <param name="si">The service interface this grain reference type is being generated for</param>
        /// <param name="isFactory">whether the class being generated is a factory class rather than a grainref implementation</param>
        /// <param name="referenceClass">The class being generated for this grain reference type</param>
        protected override void AddCastMethods(GrainInterfaceData si, bool isFactory, CodeTypeDeclaration referenceClass)
        {
            throw new NotImplementedException("AddCastMethods");
        }

        protected override string GetBasicMethodImpl(MethodInfo methodInfo)
        {
            throw new NotImplementedException("GetBasicMethodImpl");
        }

        /// <summary>
        /// Generates a wrapper method that takes arguments of the original method.
        /// </summary>
        protected override CodeTypeMember GetBasicReferenceMethod(MethodInfo methodInfo, CodeTypeParameterCollection genericTypeParam, bool isObserver)
        {
            throw new NotImplementedException("GetBasicReferenceMethod");
        }
        
        /// <summary>
        /// Generate any safeguard check statements for the generated Invoke for the specified method
        /// </summary>
        /// <param name="methodInfo">The method for which the invoke is being generated for </param>
        /// <returns></returns>
        protected override string GetParamGuardCheckStatements(MethodInfo methodInfo)
        {
            throw new NotImplementedException("GetParamGuardCheckStatements");
        }

        protected override string GetGenericTypeName(Type type)
        {
            throw new NotImplementedException("GetGenericTypeName");
        }

        /// <summary>
        /// Returns the F# name for the provided <paramref name="parameter"/>.
        /// </summary>
        /// <param name="parameter">The parameter.</param>
        /// <returns>The F# name for the provided <paramref name="parameter"/>.</returns>
        protected override string GetParameterName(ParameterInfo parameter)
        {
            return string.Format("``{0}``", GrainInterfaceData.GetParameterName(parameter));
        }

        #endregion
    }
}
