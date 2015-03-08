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
using System.Threading.Tasks;

using Orleans.CodeGeneration.Serialization;
using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Visual Basic code generator. It contains the VB-specific logic for code generation of 
    /// state classes, factories, grain reference classes, and method invokers. 
    /// </summary>
    internal class VBCodeGenerator : NamespaceGenerator
    {
        public VBCodeGenerator(Assembly grainAssembly, string nameSpace)
            : base(grainAssembly, nameSpace, Language.VisualBasic)
        {}

        protected override string FixupTypeName(string str)
        {
            return str.Replace("<", "(Of ").Replace(">", ")");
        }

        protected override string GetGenericTypeName(Type type, Action<Type> referred, Func<Type, bool> noNamespace = null)
        {
            if (type == null) throw new ArgumentNullException("type");
            if (noNamespace == null)
                noNamespace = t => true;

            referred(type);

            var name = (noNamespace(type) && !type.IsNested) ? 
                    TypeUtils.GetTemplatedName(type, language: Language.VisualBasic) 
                    : TypeUtils.GetFullName(type, Language.VisualBasic);

            if (!type.IsGenericType)
            {
                if (type.FullName == null)
                    return TypeUtils.GetTemplatedName(type, language: Language.VisualBasic);

                var result = GetNestedClassName(name);
                return result == "Void" ? "void" : result;
            }

            var builder = new StringBuilder();
            int index = name.IndexOf("`", StringComparison.Ordinal);
            builder.Append(GetNestedClassName(name.Substring(0, index), noNamespace(type)));
            builder.Append("(Of ");
            bool isFirstArgument = true;
            foreach (Type argument in type.GetGenericArguments())
            {
                if (!isFirstArgument)
                    builder.Append(',');

                builder.Append(GetGenericTypeName(argument, referred, noNamespace));
                isFirstArgument = false;
            }
            builder.Append(')');
            return builder.ToString();
        }


        #region Grain State Classes
        protected override void GenerateStateClassProperty(CodeTypeDeclaration stateClass, PropertyInfo propInfo, string name, string type)
        {
            var text = string.Format(@"Public Property {0} As {1} Implements {2}.{0}", name, type, GetGenericTypeName(propInfo.DeclaringType));
            stateClass.Members.Add(new CodeSnippetTypeMember(text));
        }
        protected override void GenerateToString(CodeTypeDeclaration stateClass, string stateClassName, Dictionary<string, string> properties)
        {
            int i = 0;
            var text = string.Format(@"
            Public Overrides Function ToString() As System.String           
                Return System.String.Format(""{0}( {1})""{2})
            End Function",
                stateClassName,
                properties.Keys.ToStrings(p => p + "={" + i++ + "} ", ""),
                properties.Keys.ToStrings(p => p, ", "));

            stateClass.Members.Add(new CodeSnippetTypeMember(text));
        }

        protected override void GenerateSetAll(CodeTypeDeclaration stateClass, Dictionary<string, string> properties)
        {
            var snippet = new StringBuilder();
            var setAllBody = @"If values Is Nothing Then
                InitStateFields()
                Exit Sub
            End If
            Dim value As Object = Nothing";

            foreach (var pair in properties)
            {
                setAllBody += @"
                ";

                if ("long".Equals(pair.Value, StringComparison.OrdinalIgnoreCase)
                    || "int64".Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    setAllBody += string.Format(
@"If values.TryGetValue(""{0}"", value) Then
    {0} = If(TypeOf (value) Is Int32, CType(value, Int64), CType(value, Int64))
End If", pair.Key);
                }
                else if ("int".Equals(pair.Value, StringComparison.OrdinalIgnoreCase)
                    || "int32".Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    setAllBody += string.Format(
@"If values.TryGetValue(""{0}"", value) Then
    {0} = If(TypeOf (value) Is Int64, CType(CType(value, Int64), Int32), CType(value, Int32))
End If", pair.Key);
                }
                else
                {
                    setAllBody += string.Format(
@"If values.TryGetValue(""{0}"", value) Then
    {0} = CType(value, {1})
End If", pair.Key, pair.Value);
                }
            }
            snippet.AppendFormat(@"
            Public Overrides Sub SetAll(values As System.Collections.Generic.IDictionary(Of String,object))              
                {0}
            End Sub", setAllBody);

            stateClass.Members.Add(new CodeSnippetTypeMember(snippet.ToString()));
        }
        #endregion

        #region Grain Interfaces
        protected override void AddCreateObjectReferenceMethods(GrainInterfaceData grainInterfaceData, CodeTypeDeclaration factoryClass)
        {
            var fieldImpl = @"Private Shared methodInvoker As Global.Orleans.CodeGeneration.IGrainMethodInvoker";
            var invokerField = new CodeSnippetTypeMember(fieldImpl);
            factoryClass.Members.Add(invokerField);

            var methodImpl = String.Format(@"
        Public Shared Async Function CreateObjectReference(obj As {0}) As System.Threading.Tasks.Task(Of {0})       
            If methodInvoker Is Nothing Then : methodInvoker = New {2}() : End If
            Return {1}.Cast(Await Global.Orleans.Runtime.GrainReference.CreateObjectReference(obj, methodInvoker))
        End Function",
            grainInterfaceData.TypeName,
            grainInterfaceData.FactoryClassName,
            grainInterfaceData.InvokerClassName);
            var createObjectReferenceMethod = new CodeSnippetTypeMember(methodImpl);
            factoryClass.Members.Add(createObjectReferenceMethod);

            methodImpl = String.Format(@"
        Public Shared Function DeleteObjectReference(reference As {0}) As System.Threading.Tasks.Task
            Return Global.Orleans.Runtime.GrainReference.DeleteObjectReference(reference)
        End Function",
            grainInterfaceData.TypeName);
            var deleteObjectReferenceMethod = new CodeSnippetTypeMember(methodImpl);
            factoryClass.Members.Add(deleteObjectReferenceMethod);
        }

        protected override string GetOrleansGetMethodNameImpl(Type grainType, GrainInterfaceInfo grainInterfaceInfo)
        {
            if (grainInterfaceInfo.Interfaces.Keys.Count == 0)
            {
                // No public method is implemented in this grain type for orleans messages
                var nullInvokeMethod = @"
                Throw New NotImplementedException()
                ";

                return nullInvokeMethod;
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
                    var methodInfo = interfaceInfo.Methods[methodId];

                    //add return type assembly and namespace in
                    GetGenericTypeName(methodInfo.ReturnType);

                    var invokeGrainMethod = string.Format("Return \"{0}\"", methodInfo.Name);
                    methodSwitchBody += string.Format(
                    @"Case {0}
                            {1}
                    "
                    , methodId, invokeGrainMethod);
                }

                interfaceSwitchBody += String.Format(@"
                Case {0}  ' {1}
                    Select methodId
                        {2}

                        Case Else 
                            Throw New NotImplementedException(""interfaceId=""+interfaceId+"",methodId=""+methodId)
                    End Select
",
                interfaceId, interfaceInfo.InterfaceType.Name, methodSwitchBody);
            } // End for each interface

            return string.Format(@"
            Select interfaceId
                {0}
                Case Else
                    Throw New System.InvalidCastException(""interfaceId=""+interfaceId)
            End Select",
            interfaceSwitchBody);
        }

        protected override string GetInvokerImpl(GrainInterfaceData si, CodeTypeDeclaration invokerClass, Type grainType, GrainInterfaceInfo grainInterfaceInfo, bool isClient)
        {
            //No public method is implemented in this grain type for orleans messages
            if (grainInterfaceInfo.Interfaces.Count == 0)
            {
                return string.Format(@"
            Dim t = New System.Threading.Tasks.TaskCompletionSource(Of Object)()
            t.SetException(New NotImplementedException(""No grain interfaces for type {0}""))
            Return t.Task
                ", TypeUtils.GetFullName(grainType, Language.VisualBasic));
            }

            var builder = new StringBuilder();
            builder.Append(@"            Try
            ");

            var interfaceSwitchBody = String.Empty;
            foreach (int interfaceId in grainInterfaceInfo.Interfaces.Keys)
            {
                InterfaceInfo interfaceInfo = grainInterfaceInfo.Interfaces[interfaceId];
                interfaceSwitchBody += GetMethodDispatchSwitchForInterface(interfaceId, interfaceInfo);
            }

            builder.AppendFormat(
                @"If grain Is Nothing Then : Throw New System.ArgumentNullException(""grain"") : End If
                Select Case interfaceId
                    {0}
                    Case Else
                        {1}
                End Select", interfaceSwitchBody, "Throw New System.InvalidCastException(\"interfaceId=\"+interfaceId)");

            builder.Append(@"
            Catch ex As Exception
                Dim t = New System.Threading.Tasks.TaskCompletionSource(Of Object)()
                t.SetException(ex)
                Return t.Task
            End Try");

            return builder.ToString();
        }

        private string GetMethodDispatchSwitchForInterface(int interfaceId, InterfaceInfo interfaceInfo)
        {
            var methodSwitchBody = String.Empty;

            foreach (int methodId in interfaceInfo.Methods.Keys)
            {
                var methodInfo = interfaceInfo.Methods[methodId];
                var returnType = methodInfo.ReturnType;
                GetGenericTypeName(returnType); // Adds return type assembly and namespace to import / library lists if necessary
                var invokeGrainArgs = string.Empty;

                ParameterInfo[] paramInfoArray = methodInfo.GetParameters();
                for (int count = 0; count < paramInfoArray.Length; count++)
                {
                    invokeGrainArgs += string.Format("CType(arguments({1}),{0})",
                        GetGenericTypeName(paramInfoArray[count].ParameterType), count);
                    if (count < paramInfoArray.Length - 1)
                        invokeGrainArgs += ", ";
                }

                // todo: parameters for indexed properties
                var grainTypeName = GetGenericTypeName(interfaceInfo.InterfaceType);
                var methodName = methodInfo.Name;

                string invokeGrainMethod;
                if (!methodInfo.IsSpecialName)
                {
                    invokeGrainMethod = string.Format("CType(grain,{0}).{1}({2})", grainTypeName, methodName, invokeGrainArgs);

                }
                else if (methodInfo.Name.StartsWith("get_"))
                {
                    invokeGrainMethod = string.Format("CType(grain,{0}).{1}", grainTypeName, methodName.Substring(4));
                }
                else if (methodInfo.Name.StartsWith("set_"))
                {
                    invokeGrainMethod = string.Format("CType(grain,{0}).{1} = {2}", grainTypeName, methodName.Substring(4), invokeGrainArgs);
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
                        @"{0}
Return System.Threading.Tasks.Task.FromResult(CObj(True))
",
                        invokeGrainMethod);
                }
                else if (GrainInterfaceData.IsTaskType(returnType))
                {
                    if (returnType == typeof(Task))
                    {
                        caseBodyStatements = string.Format(
                            @"    Return {0}.ContinueWith(Function(t)                                    
                                    If t.Status = System.Threading.Tasks.TaskStatus.Faulted Then : Throw t.Exception : End If
                                    Return Nothing
                                End Function)
",
                            invokeGrainMethod);
                    }
                    else
                        caseBodyStatements = string.Format(
                            @"Return {0}.ContinueWith(Function(t) CObj(t.Result))
",
                            invokeGrainMethod);
                }
                else
                {
                    // Should never happen
                    throw new InvalidOperationException(string.Format(
                        "Don't know how to create invoker for method {0} with Id={1} of returnType={2}", methodInfo, methodId, returnType));
                }

                methodSwitchBody += string.Format(@"                            Case {0} 
                                {1}", methodId, caseBodyStatements);
            }

            var defaultCase = @"                            Case Else 
                            Throw New NotImplementedException(""interfaceId=""+interfaceId+"",methodId=""+methodId)";

            return String.Format(@"Case {0}  ' {1}
                        Select Case methodId
{2}                            
{3}
                        End Select
",
            interfaceId, interfaceInfo.InterfaceType.Name, methodSwitchBody, defaultCase);
        }

        protected override void AddGetGrainMethods(GrainInterfaceData iface, CodeTypeDeclaration factoryClass)
        {
            RecordReferencedNamespaceAndAssembly(typeof(GrainId));
            RecordReferencedNamespaceAndAssembly(iface.Type);
            var interfaceId = GrainInterfaceData.GetGrainInterfaceId(iface.Type);
            var interfaceName = iface.InterfaceTypeName;

            Action<string> add = codeFmt =>
                factoryClass.Members.Add(new CodeSnippetTypeMember(
                     String.Format(codeFmt, FixupTypeName(interfaceName), interfaceId)));

            bool hasKeyExt = GrainInterfaceData.UsesPrimaryKeyExtension(iface.Type);

            bool isGuidKey = typeof(IGrainWithGuidKey).IsAssignableFrom(iface.Type);
            bool isLongKey = typeof(IGrainWithIntegerKey).IsAssignableFrom(iface.Type);
            bool isStringKey = typeof(IGrainWithStringKey).IsAssignableFrom(iface.Type);
            bool isDefaultKey = !(isGuidKey || isStringKey || isLongKey);

            if (isDefaultKey && hasKeyExt)
            {
                // the programmer has specified [ExtendedPrimaryKey] on the interface.
                add(
                    @"
                        Public Shared Function GetGrain(primaryKey as System.Int64, keyExt as System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(GetType({0}), {1}, primaryKey, keyExt))
                        End Function");
                add(
                    @"
                        Public Shared Function GetGrain(primaryKey as System.Int64, keyExt as System.String, grainClassNamePrefix As System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(GetType({0}), {1}, primaryKey, keyExt, grainClassNamePrefix))
                        End Function");

                add(
                    @"
                        Public Shared Function GetGrain(primaryKey As System.Guid, keyExt as System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(GetType({0}), {1}, primaryKey, keyExt))
                        End Function");
                add(
                    @"
                        Public Shared Function GetGrain(primaryKey As System.Guid, keyExt as System.String, grainClassNamePrefix As System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeKeyExtendedGrainReferenceInternal(GetType({0}), {1}, primaryKey, keyExt,grainClassNamePrefix))
                        End Function");
            }
            else
            {
                // the programmer has not specified [ExplicitPlacement] on the interface nor [ExtendedPrimaryKey].
                if (isLongKey || isDefaultKey)
                {
                add(
                    @"
                        Public Shared Function GetGrain(primaryKey as System.Int64) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(GetType({0}), {1}, primaryKey))
                        End Function");
                add(
                    @"
                        Public Shared Function GetGrain(primaryKey as System.Int64, grainClassNamePrefix As System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(GetType({0}), {1}, primaryKey, grainClassNamePrefix))
                        End Function");
                }
                if (isGuidKey || isDefaultKey)
                {
                add(
                    @"
                        Public Shared Function GetGrain(primaryKey As System.Guid) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(GetType({0}), {1}, primaryKey))
                        End Function");
                add(
                    @"
                        Public Shared Function GetGrain(primaryKey As System.Guid, grainClassNamePrefix As System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(GetType({0}), {1}, primaryKey, grainClassNamePrefix))
                        End Function");
                }
                if (isStringKey)
                {
                    add(
                        @"
                        Public Shared Function GetGrain(primaryKey As System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(GetType({0}), {1}, primaryKey))
                        End Function");
                    add(
                        @"
                        Public Shared Function GetGrain(primaryKey As System.String, grainClassNamePrefix As System.String) As {0}
                            Return Cast(Global.Orleans.CodeGeneration.GrainFactoryBase.MakeGrainReferenceInternal(GetType({0}), {1}, primaryKey, grainClassNamePrefix))
                        End Function");
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
                castImplCode = string.Format(@"{0}.Cast(grainRef)", FixupTypeName(si.ReferenceClassName));

                if (si.IsSystemTarget)
                    checkCode = 
                        @"If  Not ((Global.Orleans.Runtime.GrainReference)grainRef).IsInitializedSystemTarget Then
                            Throw New InvalidOperationException(""InvalidCastException cast of a system target grain reference. Must have SystemTargetSilo set to the target silo address"")
                          End If";
            }
            else
            {
                castImplCode = string.Format(
                    @"CType(Global.Orleans.Runtime.GrainReference.CastInternal(GetType({0}), Function(gr As GrainReference) New {1}(gr), grainRef, {2}),{0})",
                    FixupTypeName(si.InterfaceTypeName), // Interface type for references for this grain
                    FixupTypeName(si.ReferenceClassName), // Concrete class for references for this grain
                    GrainInterfaceData.GetGrainInterfaceId(si.Type));
            }

            var methodImpl = string.Format(@"
            {3} Shared Function Cast(grainRef As Global.Orleans.Runtime.IAddressable) As {0}           
                {1}
                Return {2}
            End Function",
                si.InterfaceTypeName.Replace("<", "(Of ").Replace(">", ")"),
                checkCode,
                castImplCode,
                "Public");

            string getSystemTarget = null;
            if (isFactory && si.IsSystemTarget)
            {
                getSystemTarget = string.Format(@"
            Friend Shared Function GetSystemTarget(grainId As Global.Orleans.Runtime.GrainId, silo As Global.Orleans.Runtime.SiloAddress) As {0}
                Return Global.Orleans.Runtime.GrainReference.GetSystemTarget(grainId, silo, Cast)
            End Function",
                FixupTypeName(si.InterfaceTypeName));
            }

            var castMethod = new CodeSnippetTypeMember(methodImpl + getSystemTarget);
            referenceClass.Members.Add(castMethod);
        }

        protected override string GetInvokeArguments(MethodInfo methodInfo)
        {
            var invokeArguments = string.Empty;
            int count = 1;
            var parameters = methodInfo.GetParameters();
            foreach (ParameterInfo paramInfo in parameters)
            {
                if (paramInfo.ParameterType.GetInterface("Orleans.Runtime.IAddressable") != null && !typeof(GrainReference).IsAssignableFrom(paramInfo.ParameterType))
                    invokeArguments += string.Format("If(typeof({0}) is Global.Orleans.Grain,{2}.{1}.Cast({0}.AsReference()),{0})",
                        GetParameterName(paramInfo),
                        GrainInterfaceData.GetFactoryClassForInterface(paramInfo.ParameterType, Language.VisualBasic),
                        paramInfo.ParameterType.Namespace);
                else
                    invokeArguments += GetParameterName(paramInfo);

                if (count++ < parameters.Length)
                    invokeArguments += ", ";
            }
            return invokeArguments;
        }

        protected override string GetBasicMethodImpl(MethodInfo methodInfo)
        {
            var invokeArguments = GetInvokeArguments(methodInfo);

            int methodId = GrainInterfaceData.ComputeMethodId(methodInfo);
            string methodImpl;
            string optional = null;

            if (GrainInterfaceData.IsReadOnly(methodInfo))
                optional = ", options:= Global.Orleans.CodeGeneration.InvokeMethodOptions.ReadOnly";
            
            if (GrainInterfaceData.IsUnordered(methodInfo))
            {
                if (optional == null)
                    optional = ", options:= ";
                else
                    optional += " | ";

                optional += " Global.Orleans.CodeGeneration.InvokeMethodOptions.Unordered";
            }

            if (GrainInterfaceData.IsAlwaysInterleave(methodInfo))
            {
                if (optional == null)
                    optional = ", options:= ";
                else
                    optional += " | ";

                optional += " Global.Orleans.CodeGeneration.InvokeMethodOptions.AlwaysInterleave";
            }

            if (methodInfo.ReturnType == typeof(void))
            {
                methodImpl = string.Format(@"
                MyBase.InvokeOneWayMethod({0}, New System.Object() {{{1}}} {2})",
                methodId, invokeArguments, optional);
            }
            else
            {
                if (methodInfo.ReturnType == typeof (Task))
                {
                    methodImpl = string.Format(@"
                Return MyBase.InvokeMethodAsync(Of System.Object)({0}, New System.Object() {{{1}}} {2})",
                        methodId,
                        invokeArguments,
                        optional);
                }
                else
                {
                    methodImpl = string.Format(@"
                Return MyBase.InvokeMethodAsync(Of {0})({1}, New System.Object() {{{2}}} {3})",
                        GetActualMethodReturnType(methodInfo.ReturnType, SerializeFlag.NoSerialize),
                        methodId,
                        invokeArguments,
                        optional);
                }
            }
            return GetParamGuardCheckStatements(methodInfo) + methodImpl;
        }

        /// <summary>
        /// Generates a wrapper method that takes arguments of the original method.
        /// </summary>
        protected override CodeTypeMember GetBasicReferenceMethod(MethodInfo methodInfo, CodeTypeParameterCollection genericTypeParam, bool isObserver)
        {
            SerializerGenerationManager.RecordTypeToGenerate(methodInfo.ReturnType);
            foreach (ParameterInfo paramInfo in methodInfo.GetParameters())
                SerializerGenerationManager.RecordTypeToGenerate(paramInfo.ParameterType);

            if (!isObserver)
            {
                var parameterList = new StringBuilder();
                var first = true;
                foreach (var p in methodInfo.GetParameters())
                {
                    if (!first)
                        parameterList.Append(", ");
                    first = false;
                    parameterList.AppendFormat("{0} As {1}", p.Name, GetGenericTypeName(p.ParameterType, type => { }, t => false));
                }

                var snippet = new StringBuilder();
                snippet.AppendFormat("Public Function {0}({1}) As {2} Implements {3}.{0}",
                    methodInfo.Name,
                    parameterList,
                    GetGenericTypeName(methodInfo.ReturnType, type => { }, t => false),
                    GetGenericTypeName(methodInfo.DeclaringType, type => { }, t => false))
                    .AppendLine();
                snippet.AppendFormat("            {0}", GetBasicMethodImpl(methodInfo)).AppendLine();
                snippet.AppendLine("        End Function");
                return new CodeSnippetTypeMember(snippet.ToString());
            }

            var referenceMethod = new CodeMemberMethod
            {
                Name = methodInfo.Name,
                ReturnType = GetReturnTypeReference(methodInfo.ReturnType, SerializeFlag.DeserializeResult)
            };

            foreach (var paramInfo in methodInfo.GetParameters())
                referenceMethod.Parameters.Add(new CodeParameterDeclarationExpression(
                    new CodeTypeReference(paramInfo.ParameterType),GetParameterName(paramInfo)));

            referenceMethod.Attributes = MemberAttributes.Public | MemberAttributes.Final;
            var pit = new CodeTypeReference(GetGenericTypeName(methodInfo.DeclaringType, type => { }, t => false));
            referenceMethod.PrivateImplementationType = pit;

            var methodImpl = new CodeSnippetStatement(GetBasicMethodImpl(methodInfo));
            referenceMethod.Statements.Add(methodImpl);
            return referenceMethod;
        }
        
        /// <summary>
        /// Generate any safeguard check statements for the generated Invoke for the specified method
        /// </summary>
        /// <param name="methodInfo">The method for which the invoke is being generated for </param>
        /// <returns></returns>
        protected override string GetParamGuardCheckStatements(MethodInfo methodInfo)
        {
            var paramGuardStatements = new StringBuilder();
            foreach (ParameterInfo p in methodInfo.GetParameters())
            {
                // For any parameters of type IGrainObjerver, the object passed at runtime must also be a GrainReference
                if (typeof (IGrainObserver).IsAssignableFrom(p.ParameterType))
                    paramGuardStatements.AppendLine(string.Format(
                            @"Global.Orleans.CodeGeneration.GrainFactoryBase.CheckGrainObserverParamInternal({0})",
                            GetParameterName(p)));
            }
            return paramGuardStatements.ToString();
        }

        protected override string GetGenericTypeName(Type type)
        {
            // Add in the namespace of the type and the assembly file in which the type is defined
            AddReferencedAssembly(type);
            // Add in the namespace of the type and the assembly file in which any generic argument types are defined
            if (type.IsGenericType)
                foreach (Type argument in type.GetGenericArguments())
                    AddReferencedAssembly(argument);

            var typeName = TypeUtils.GetTemplatedName(type, t => CurrentNamespace != t.Namespace && !ReferencedNamespaces.Contains(t.Namespace), Language.VisualBasic);
            typeName = GetNestedClassName(typeName);
            typeName = typeName.Replace("<", "(Of ").Replace(">", ")");
            return typeName;
        }

        /// <summary>
        /// Returns the Visual Basic name for the provided <paramref name="parameter"/>.
        /// </summary>
        /// <param name="parameter">The parameter.</param>
        /// <returns>The Visual Basic name for the provided <paramref name="parameter"/>.</returns>
        protected override string GetParameterName(ParameterInfo parameter)
        {
            return string.Format("[{0}]", GrainInterfaceData.GetParameterName(parameter));
        }

        #endregion
    }
}
