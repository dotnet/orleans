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
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// C# code generator. It contains the C#-specific logic for code generation of 
    /// state classes, factories, grain reference classes, and method invokers. 
    /// </summary>
    internal class CSharpCodeGenerator : NamespaceGenerator
    {
        public CSharpCodeGenerator(Assembly grainAssembly, string nameSpace)
            : base(grainAssembly, nameSpace, Language.CSharp)
        {}

        protected override string GetGenericTypeName(Type type, Action<Type> referred, Func<Type, bool> noNamespace = null)
        {
            if (type == null) throw new ArgumentNullException("type");
            if (noNamespace == null)
                noNamespace = t => true;

            referred(type);

            var name = (noNamespace(type) && !type.IsNested) ? type.Name : TypeUtils.GetFullName(type, Language.CSharp);

            if (!type.IsGenericType)
            {
                if (type.FullName == null) return type.Name;

                var result = GetNestedClassName(name);
                return result == "Void" ? "void" : result;
            }

            var builder = new StringBuilder();
            int index = name.IndexOf("`", StringComparison.Ordinal);
            builder.Append(GetNestedClassName(name.Substring(0, index), noNamespace(type)));
            builder.Append('<');
            bool isFirstArgument = true;

            foreach (Type argument in type.GetGenericArguments())
            {
                if (!isFirstArgument)
                    builder.Append(',');

                builder.Append(GetGenericTypeName(argument, referred, noNamespace));
                isFirstArgument = false;
            }
            builder.Append('>');
            return builder.ToString();
        }

        /// <summary>
        /// Returns the C# name for the provided <paramref name="parameter"/>.
        /// </summary>
        /// <param name="parameter">The parameter.</param>
        /// <returns>The C# name for the provided <paramref name="parameter"/>.</returns>
        protected override string GetParameterName(ParameterInfo parameter)
        {
            return "@" + GrainInterfaceData.GetParameterName(parameter);
        }

        #region Grain State Classes
        protected override void GenerateStateClassProperty(CodeTypeDeclaration stateClass, PropertyInfo propInfo, string name, string type)
        {
            var text = string.Format(@"
            public {1} @{0} {{ get; set; }}",
                    name, type);
            stateClass.Members.Add(new CodeSnippetTypeMember(text));
        }

        protected override void GenerateToString(CodeTypeDeclaration stateClass, string stateClassName, Dictionary<string, string> properties)
        {
            int i = 0;
            var text = string.Format(@"
            public override System.String ToString()
            {{
                return System.String.Format(""{0}( {1})""{2});
            }}", stateClassName, properties.Keys.ToStrings(p => p + "={" + i++ + "} ", ""), properties.Keys.ToStrings(p => "@" + p, ", "));

            stateClass.Members.Add(new CodeSnippetTypeMember(text));
        }

        protected override void GenerateSetAll(CodeTypeDeclaration stateClass, Dictionary<string, string> properties)
        {
            var snippet = new StringBuilder();
            var setAllBody = @"object value;
                if (values == null) { InitStateFields(); return; }";

            foreach (var pair in properties)
            {
                setAllBody += @"
                ";

                if ("long".Equals(pair.Value, StringComparison.OrdinalIgnoreCase)
                    || "int64".Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    setAllBody += string.Format(
@"if (values.TryGetValue(""{0}"", out value)) @{0} = value is Int32 ? (Int32)value : (Int64)value;",
                                     pair.Key);
                }
                else if ("int".Equals(pair.Value, StringComparison.OrdinalIgnoreCase)
                    || "int32".Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    setAllBody += string.Format(
@"if (values.TryGetValue(""{0}"", out value)) @{0} = value is Int64 ? (Int32)(Int64)value : (Int32)value;",
                                     pair.Key);
                }
                else
                {
                    setAllBody += string.Format(
@"if (values.TryGetValue(""{0}"", out value)) @{0} = ({1}) value;",
                                     pair.Key, pair.Value);
                }
            }

            snippet.AppendFormat(@"
            public override void SetAll(System.Collections.Generic.IDictionary<string,object> values)
            {{   
                {0}
            }}", setAllBody);

            stateClass.Members.Add(new CodeSnippetTypeMember(snippet.ToString()));
        }
        #endregion

        #region Grain Interfaces
        protected override void AddCreateObjectReferenceMethods(GrainInterfaceData grainInterfaceData, CodeTypeDeclaration factoryClass)
        {
            var fieldImpl = @"
        private static global::Orleans.CodeGeneration.IGrainMethodInvoker methodInvoker;";
            var invokerField = new CodeSnippetTypeMember(fieldImpl);
            factoryClass.Members.Add(invokerField);

            var methodImpl = String.Format(@"
        public async static System.Threading.Tasks.Task<{0}> CreateObjectReference({0} obj)
        {{
            if (methodInvoker == null) methodInvoker = new {2}();
            return {1}.Cast(await global::Orleans.Runtime.GrainReference.CreateObjectReference(obj, methodInvoker));
        }}", grainInterfaceData.TypeName, grainInterfaceData.FactoryClassName, grainInterfaceData.InvokerClassName);
            var createObjectReferenceMethod = new CodeSnippetTypeMember(methodImpl);
            factoryClass.Members.Add(createObjectReferenceMethod);

            methodImpl = String.Format(@"
        public static System.Threading.Tasks.Task DeleteObjectReference({0} reference)
        {{
            return global::Orleans.Runtime.GrainReference.DeleteObjectReference(reference);
        }}",
            grainInterfaceData.TypeName);
            var deleteObjectReferenceMethod = new CodeSnippetTypeMember(methodImpl);
            factoryClass.Members.Add(deleteObjectReferenceMethod);
        }

        protected override string GetInvokerImpl(
            GrainInterfaceData si, 
            CodeTypeDeclaration invokerClass, 
            Type grainType, 
            GrainInterfaceInfo grainInterfaceInfo, 
            bool isClient)
        {
            //No public method is implemented in this grain type for orleans messages
            if (grainInterfaceInfo.Interfaces.Count == 0)
            {
                return string.Format(@"
                var t = new System.Threading.Tasks.TaskCompletionSource<object>();
                t.SetException(new NotImplementedException(""No grain interfaces for type {0}""));
                return t.Task;
                ", TypeUtils.GetFullName(grainType, Language.CSharp));
            }

            var builder = new StringBuilder();
            builder.AppendFormat(@"
            try
            {{");

            var interfaceSwitchBody = String.Empty;
            foreach (int interfaceId in grainInterfaceInfo.Interfaces.Keys)
            {
                InterfaceInfo interfaceInfo = grainInterfaceInfo.Interfaces[interfaceId];
                interfaceSwitchBody += GetMethodDispatchSwitchForInterface(interfaceId, interfaceInfo);
            }

            builder.AppendFormat(
                @"                    if (grain == null) throw new System.ArgumentNullException(""grain"");
                switch (interfaceId)
                {{
                    {0}
                    default:
                        {1};
                }}", interfaceSwitchBody, "throw new System.InvalidCastException(\"interfaceId=\"+interfaceId)");

            builder.AppendFormat(@"
            }}
            catch(Exception ex)
            {{
                var t = new System.Threading.Tasks.TaskCompletionSource<object>();
                t.SetException(ex);
                return t.Task;
            }}");

            return builder.ToString();
        }

        private string GetMethodDispatchSwitchForInterface(int interfaceId, InterfaceInfo interfaceInfo)
        {
            string methodSwitchBody = String.Empty;

            foreach (int methodId in interfaceInfo.Methods.Keys)
            {
                MethodInfo methodInfo = interfaceInfo.Methods[methodId];
                var returnType = methodInfo.ReturnType;
                GetGenericTypeName(returnType); // Adds return type assembly and namespace to import / library lists if necessary
                var invokeGrainArgs = string.Empty;

                ParameterInfo[] paramInfoArray = methodInfo.GetParameters();
                for (int count = 0; count < paramInfoArray.Length; count++)
                {
                    invokeGrainArgs += string.Format("({0})arguments[{1}]",
                        GetGenericTypeName(paramInfoArray[count].ParameterType), count);

                    if (count < paramInfoArray.Length - 1)
                        invokeGrainArgs += ", ";
                }

                // TODO: parameters for indexed properties
                string grainTypeName = GetGenericTypeName(interfaceInfo.InterfaceType);
                string methodName = methodInfo.Name;

                string invokeGrainMethod;
                if (!methodInfo.IsSpecialName)
                {
                    invokeGrainMethod = string.Format("(({0})grain).{1}({2})", grainTypeName, methodName, invokeGrainArgs);
                }
                else if (methodInfo.Name.StartsWith("get_"))
                {
                    invokeGrainMethod = string.Format("(({0})grain).{1}", grainTypeName, methodName.Substring(4));
                }
                else if (methodInfo.Name.StartsWith("set_"))
                {
                    invokeGrainMethod = string.Format("(({0})grain).{1} = {2}", grainTypeName, methodName.Substring(4), invokeGrainArgs);
                }
                else
                {
                    // Should never happen
                    throw new InvalidOperationException("Don't know how to handle method " + methodInfo);
                }

                string caseBodyStatements;
                if (returnType == typeof(void))
                {
                    caseBodyStatements = string.Format(
                        @"{0}; return System.Threading.Tasks.Task.FromResult((object)true);
",
                        invokeGrainMethod);
                }
                else if (GrainInterfaceData.IsTaskType(returnType))
                {
                    if (returnType != typeof(Task))
                        GetGenericTypeName(returnType.GetGenericArguments()[0]);

                    if (returnType == typeof(Task))
                    {
                        caseBodyStatements = string.Format(
                            @"return {0}.ContinueWith(t => {{if (t.Status == System.Threading.Tasks.TaskStatus.Faulted) throw t.Exception; return (object)null; }});
",
                            invokeGrainMethod);
                    }
                    else
                        caseBodyStatements = string.Format(
                            @"return {0}.ContinueWith(t => {{if (t.Status == System.Threading.Tasks.TaskStatus.Faulted) throw t.Exception; return (object)t.Result; }});
",
                            invokeGrainMethod);
                }
                else
                {
                    // Should never happen
                    throw new InvalidOperationException(string.Format(
                        "Don't know how to create invoker for method {0} with Id={1} of returnType={2}", methodInfo, methodId, returnType));
                }

                methodSwitchBody += string.Format(@"                            case {0}: 
                                {1}", methodId, caseBodyStatements);
            }

            const string defaultCase = @"default: 
                            throw new NotImplementedException(""interfaceId=""+interfaceId+"",methodId=""+methodId);";

            return String.Format(@"case {0}:  // {1}
                        switch (methodId)
                        {{
{2}                            {3}
                        }}",
            interfaceId, interfaceInfo.InterfaceType.Name, methodSwitchBody, defaultCase);
        }

        protected override string GetOrleansGetMethodNameImpl(Type grainType, GrainInterfaceInfo grainInterfaceInfo)
        {
            if (grainInterfaceInfo.Interfaces.Keys.Count == 0)
            {
                // No public method is implemented in this grain type for orleans messages
                return @"
                throw new NotImplementedException();
                ";
            }

            var interfaces = new Dictionary<int, InterfaceInfo>(grainInterfaceInfo.Interfaces); // Copy, as we may alter the original collection in the loop below
            var interfaceSwitchBody = String.Empty;

            foreach (var kv in interfaces)
            {
                var methodSwitchBody = String.Empty;
                int interfaceId = kv.Key;
                InterfaceInfo interfaceInfo = kv.Value;

                foreach (int methodId in interfaceInfo.Methods.Keys)
                {
                    MethodInfo methodInfo = interfaceInfo.Methods[methodId];

                    //add return type assembly and namespace in
                    GetGenericTypeName(methodInfo.ReturnType);

                    var invokeGrainMethod = string.Format("return \"{0}\"", methodInfo.Name);
                    methodSwitchBody += string.Format(
                    @"case {0}:
                            {1};
                    ", methodId, invokeGrainMethod);
                }

                interfaceSwitchBody += String.Format(@"
                case {0}:  // {1}
                    switch (methodId)
                    {{
                        {2}
                        default: 
                            throw new NotImplementedException(""interfaceId=""+interfaceId+"",methodId=""+methodId);
                    }}", interfaceId, interfaceInfo.InterfaceType.Name, methodSwitchBody);
            } // End for each interface

            return string.Format(@"
            switch (interfaceId)
            {{
                {0}

                default:
                    throw new System.InvalidCastException(""interfaceId=""+interfaceId);
            }}", interfaceSwitchBody);
        }
        protected override void AddGetGrainMethods(GrainInterfaceData iface, CodeTypeDeclaration factoryClass)
        {
            RecordReferencedNamespaceAndAssembly(typeof(GrainId));
            RecordReferencedNamespaceAndAssembly(iface.Type);
            var interfaceId = GrainInterfaceData.GetGrainInterfaceId(iface.Type);
            Action<string> add = codeFmt => factoryClass.Members.Add(
                new CodeSnippetTypeMember(String.Format(codeFmt, iface.InterfaceTypeName, interfaceId)));

            bool isGuidCompoundKey = typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(iface.Type);
            bool isLongCompoundKey = typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(iface.Type);
            bool isGuidKey = typeof(IGrainWithGuidKey).IsAssignableFrom(iface.Type);
            bool isLongKey = typeof(IGrainWithIntegerKey).IsAssignableFrom(iface.Type);
            bool isStringKey = typeof(IGrainWithStringKey).IsAssignableFrom(iface.Type);
            bool isDefaultKey = !(isGuidKey || isStringKey || isLongKey);

            if (isLongCompoundKey)
            {
                // the programmer has specified [ExtendedPrimaryKey] on the interface.
                add(@"
                        public static {0} GetGrain(long primaryKey, string keyExt)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(typeof({0}), {1}, primaryKey, keyExt));
                        }}");

                add(@"
                        public static {0} GetGrain(long primaryKey, string keyExt, string grainClassNamePrefix)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(typeof({0}), {1}, primaryKey, keyExt, grainClassNamePrefix));
                        }}");
            }
            else if (isGuidCompoundKey)
            {
                add(@"
                        public static {0} GetGrain(System.Guid primaryKey, string keyExt)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(typeof({0}), {1}, primaryKey, keyExt));
                        }}");

                add(@"
                        public static {0} GetGrain(System.Guid primaryKey, string keyExt, string grainClassNamePrefix)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(typeof({0}), {1}, primaryKey, keyExt,grainClassNamePrefix));
                        }}");
            }
            else
            {
                // the programmer has not specified [ExplicitPlacement] on the interface nor [ExtendedPrimaryKey].
                if (isLongKey || isDefaultKey)
                {
                    add(@"
                        public static {0} GetGrain(long primaryKey)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(typeof({0}), {1}, primaryKey));
                        }}");

                    add(@"
                        public static {0} GetGrain(long primaryKey, string grainClassNamePrefix)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(typeof({0}), {1}, primaryKey, grainClassNamePrefix));
                        }}");
                }

                if (isGuidKey || isDefaultKey)
                {
                    add(@"
                        public static {0} GetGrain(System.Guid primaryKey)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(typeof({0}), {1}, primaryKey));
                        }}");

                    add(@"
                        public static {0} GetGrain(System.Guid primaryKey, string grainClassNamePrefix)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(typeof({0}), {1}, primaryKey, grainClassNamePrefix));
                        }}");
                }

                if (isStringKey)
                {
                    add(@"
                        public static {0} GetGrain(System.String primaryKey)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(typeof({0}), {1}, primaryKey));
                        }}");

                    add(@"
                        public static {0} GetGrain(System.String primaryKey, string grainClassNamePrefix)
                        {{
                            return Cast(global::Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(typeof({0}), {1}, primaryKey, grainClassNamePrefix));
                        }}");
                }
            }
        }
        
        /// <summary>
        /// Generate Cast method in CodeDom and add it in reference class
        /// </summary>
        /// <param name="si">The service interface this grain reference type is being generated for</param>
        /// <param name="isFactory">whether the class being generated is a factory class rather than a grainref implementation</param>
        /// <param name="referenceClass">The class being generated for this grain reference type</param>
        protected override void AddCastMethods(GrainInterfaceData si, bool isFactory, CodeTypeDeclaration referenceClass)
        {
            string castImplCode;
            string checkCode = null;
            if (isFactory)
            {
                castImplCode = string.Format(@"{0}.Cast(grainRef)", si.ReferenceClassName);

                if (si.IsSystemTarget)
                    checkCode = @"if(!((global::Orleans.Runtime.GrainReference)grainRef).IsInitializedSystemTarget)
                        throw new InvalidOperationException(""InvalidCastException cast of a system target grain reference. Must have SystemTargetSilo set to the target silo address"");";
            }
            else
            {
                castImplCode = string.Format(
                    @"({0}) global::Orleans.Runtime.GrainReference.CastInternal(typeof({0}), (global::Orleans.Runtime.GrainReference gr) => {{ return new {1}(gr);}}, grainRef, {2})",
                    si.InterfaceTypeName, // Interface type for references for this grain
                    si.ReferenceClassName, // Concrete class for references for this grain
                    GrainInterfaceData.GetGrainInterfaceId(si.Type));
            }

            var methodImpl = string.Format(@"
            {3} static {0} Cast(global::Orleans.Runtime.IAddressable grainRef)
            {{
                {1}
                return {2};
            }}", si.InterfaceTypeName, checkCode, castImplCode, "public");

            var castMethod = new CodeSnippetTypeMember(methodImpl);
            referenceClass.Members.Add(castMethod);
        }

        protected override string GetInvokeArguments(MethodInfo methodInfo)
        {
            var invokeArguments = string.Empty;
            int count = 1;
            var parameters = methodInfo.GetParameters();
            foreach (var paramInfo in parameters)
            {
                if (paramInfo.ParameterType.GetInterface("Orleans.Runtime.IAddressable") != null && !typeof(GrainReference).IsAssignableFrom(paramInfo.ParameterType))
                    invokeArguments += string.Format("{0} is global::Orleans.Grain ? {0}.AsReference<{1}>() : {0}",
                        GetParameterName(paramInfo),
                        TypeUtils.GetTemplatedName(paramInfo.ParameterType, _ => true, Language.CSharp));
                else
                    invokeArguments += GetParameterName(paramInfo);

                if (count++ < parameters.Length)
                    invokeArguments += ", ";
            }
            return invokeArguments;
        }

        protected override string GetBasicMethodImpl(MethodInfo methodInfo)
        {
            string invokeArguments = GetInvokeArguments(methodInfo);
            int methodId = GrainInterfaceData.ComputeMethodId(methodInfo);
            string methodImpl;
            string optional = null;

            if (GrainInterfaceData.IsReadOnly(methodInfo))
                optional = ", options: global::Orleans.CodeGeneration.InvokeMethodOptions.ReadOnly";
            
            if (GrainInterfaceData.IsUnordered(methodInfo))
            {
                if (optional == null)
                    optional = ", options: ";
                else
                    optional += " | ";

                optional += " global::Orleans.CodeGeneration.InvokeMethodOptions.Unordered";
            }

            if (GrainInterfaceData.IsAlwaysInterleave(methodInfo))
            {
                if (optional == null)
                    optional = ", options: ";
                else
                    optional += " | ";

                optional += " global::Orleans.CodeGeneration.InvokeMethodOptions.AlwaysInterleave";
            }

            if (methodInfo.ReturnType == typeof(void))
            {
                methodImpl = string.Format(@"
                    base.InvokeOneWayMethod({0}, {1} {2});",
                    methodId, 
                    invokeArguments.Equals(string.Empty) ? "null" : String.Format("new object[] {{{0}}}", invokeArguments),
                    optional);
            }
            else
            {
                if (methodInfo.ReturnType == typeof(Task))
                {
                    methodImpl = string.Format(@"
                return base.InvokeMethodAsync<object>({0}, {1} {2});",
                        methodId,
                        invokeArguments.Equals(string.Empty) ? "null" : String.Format("new object[] {{{0}}}", invokeArguments),
                        optional);
                }
                else
                {
                    methodImpl = string.Format(@"
                return base.InvokeMethodAsync<{0}>({1}, {2} {3});",
                        GetActualMethodReturnType(methodInfo.ReturnType, SerializeFlag.NoSerialize),
                        methodId,
                        invokeArguments.Equals(string.Empty) ? "null" : String.Format("new object[] {{{0}}}", invokeArguments),
                        optional);
                }
            }
            return GetParamGuardCheckStatements(methodInfo) + methodImpl;
        }

        /// <summary>
        /// Generate any safeguard check statements for the generated Invoke for the specified method
        /// </summary>
        /// <param name="methodInfo">The method for which the invoke is being generated for </param>
        /// <returns></returns>
        protected override string GetParamGuardCheckStatements(MethodInfo methodInfo)
        {
            var paramGuardStatements = new StringBuilder();
            foreach (var parameterInfo in methodInfo.GetParameters())
            {
                // For any parameters of type IGrainObjerver, the object passed at runtime must also be a GrainReference
                if (typeof (IGrainObserver).IsAssignableFrom(parameterInfo.ParameterType))
                    paramGuardStatements.AppendLine( string.Format(
                        @"global::Orleans.CodeGeneration.GrainFactoryBase.CheckGrainObserverParamInternal({0});",
                        GetParameterName(parameterInfo)));
            }
            return paramGuardStatements.ToString();
        }
        #endregion
    }
}
