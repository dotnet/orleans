// Project Orleans Cloud Service SDK ver. 1.0
//  
// Copyright (c) .NET Foundation
// 
// All rights reserved.
//  
// MIT License
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
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
