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
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

using Orleans.CodeGeneration.Serialization;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Base class for code generators. It contains the language-agnostic logic for code generation of 
    /// factories, grain reference classes, and method invokers. 
    /// </summary>
    internal class NamespaceGenerator : CodeGeneratorBase
    {
        protected enum SerializeFlag
        {
            SerializeArgument = 0,
            DeserializeResult = 1,
            NoSerialize = 2,
        }

        private readonly HashSet<int> methodIdCollisionDetection;
        private readonly Assembly grainAssembly;
        private readonly Language language;

        internal NamespaceGenerator(Assembly grainAssembly, string nameSpace, Language language) 
            : base(language)
        {
            methodIdCollisionDetection = new HashSet<int>();
            ReferencedNamespace = new CodeNamespace(nameSpace);
            CurrentNamespace = nameSpace;
            this.grainAssembly = grainAssembly;
            this.language = language;
        }

        internal CodeNamespace ReferencedNamespace { get; private set; }

        internal void AddStateClass(GrainInterfaceData interfaceData)
        {
            GetActivationNamespace(ReferencedNamespace, interfaceData);
        }

        internal void AddReferenceClass(GrainInterfaceData interfaceData)
        {
            bool isObserver = IsObserver(interfaceData.Type);
            CodeTypeParameterCollection genericTypeParam = interfaceData.GenericTypeParams;

            // Declare factory class
            var factoryClass = new CodeTypeDeclaration(interfaceData.FactoryClassBaseName);
            if (genericTypeParam != null) 
                factoryClass.TypeParameters.AddRange(genericTypeParam);

            factoryClass.IsClass = true;
            factoryClass.TypeAttributes = interfaceData.Type.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic;
            MarkAsGeneratedCode(factoryClass);
            AddFactoryMethods(interfaceData, factoryClass);
            AddCastMethods(interfaceData, true, factoryClass);

            if (ShouldGenerateObjectRefFactory(interfaceData)) 
                AddCreateObjectReferenceMethods(interfaceData, factoryClass);

            int factoryClassIndex = ReferencedNamespace.Types.Add(factoryClass);

            var referenceClass = new CodeTypeDeclaration(interfaceData.ReferenceClassBaseName);
            if (genericTypeParam != null) 
                referenceClass.TypeParameters.AddRange(genericTypeParam);
            referenceClass.IsClass = true;

            referenceClass.BaseTypes.Add(new CodeTypeReference(typeof(GrainReference), CodeTypeReferenceOptions.GlobalReference));
            referenceClass.BaseTypes.Add(new CodeTypeReference(typeof(IAddressable), CodeTypeReferenceOptions.GlobalReference));
            var tref = new CodeTypeReference(interfaceData.Type);
            if (genericTypeParam != null)
                foreach (CodeTypeParameter tp in genericTypeParam)
                    tref.TypeArguments.Add(tp.Name);

            referenceClass.BaseTypes.Add(tref);
            
            MarkAsGeneratedCode(referenceClass);
            referenceClass.TypeAttributes = TypeAttributes.NestedAssembly;
            referenceClass.CustomAttributes.Add(
                new CodeAttributeDeclaration(new CodeTypeReference(typeof(SerializableAttribute))));

            referenceClass.CustomAttributes.Add(
                new CodeAttributeDeclaration(
                    new CodeTypeReference(typeof(GrainReferenceAttribute), CodeTypeReferenceOptions.GlobalReference),
                    new CodeAttributeArgument(
                        new CodePrimitiveExpression(interfaceData.Type.Namespace + "." + TypeUtils.GetParameterizedTemplateName(interfaceData.Type, language: language)))));

            var baseReferenceConstructor2 = new CodeConstructor {Attributes = MemberAttributes.FamilyOrAssembly};
            baseReferenceConstructor2.Parameters.Add(new CodeParameterDeclarationExpression(
                new CodeTypeReference(typeof(GrainReference), CodeTypeReferenceOptions.GlobalReference), "reference"));

            baseReferenceConstructor2.BaseConstructorArgs.Add(new CodeVariableReferenceExpression("reference"));
            referenceClass.Members.Add(baseReferenceConstructor2);

            var baseReferenceConstructor3 = new CodeConstructor {Attributes = MemberAttributes.FamilyOrAssembly};
            baseReferenceConstructor3.Parameters.Add(new CodeParameterDeclarationExpression("SerializationInfo", "info"));
            baseReferenceConstructor3.BaseConstructorArgs.Add(new CodeVariableReferenceExpression("info"));
            baseReferenceConstructor3.Parameters.Add(new CodeParameterDeclarationExpression("StreamingContext", "context"));
            baseReferenceConstructor3.BaseConstructorArgs.Add(new CodeVariableReferenceExpression("context"));
            referenceClass.Members.Add(baseReferenceConstructor3);

            var grainRef = new CodeTypeReference(typeof(GrainReference), CodeTypeReferenceOptions.GlobalReference);
            var refClassName = FixupTypeName(interfaceData.ReferenceClassName);

            // Copier, serializer, and deserializer for this type
            var copier = SerializerGenerationUtilities.GenerateCopier("_Copier", refClassName, genericTypeParam);
            copier.Statements.Add(
                new CodeMethodReturnStatement(
                    new CodeCastExpression(refClassName,
                    new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(grainRef), "CopyGrainReference", new CodeVariableReferenceExpression("input")))));
            referenceClass.Members.Add(copier);

            var serializer = SerializerGenerationUtilities.GenerateSerializer("_Serializer", refClassName, genericTypeParam);
            serializer.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeTypeReferenceExpression(grainRef),
                    "SerializeGrainReference", new CodeVariableReferenceExpression("input"), 
                    new CodeArgumentReferenceExpression("stream"),
                    new CodeArgumentReferenceExpression("expected")));
            referenceClass.Members.Add(serializer);

            var deserializer = SerializerGenerationUtilities.GenerateDeserializer("_Deserializer", refClassName, genericTypeParam);
            deserializer.Statements.Add(
                new CodeMethodReturnStatement(
                    new CodeMethodInvokeExpression(
                        new CodeTypeReferenceExpression(refClassName),
                        "Cast", 
                        new CodeCastExpression(grainRef, 
                            new CodeMethodInvokeExpression(
                                new CodeTypeReferenceExpression(grainRef),
                                "DeserializeGrainReference", 
                                new CodeArgumentReferenceExpression("expected"), new CodeArgumentReferenceExpression("stream"))))));
            referenceClass.Members.Add(deserializer);

            // this private class is the "implementation class" for the interface reference type
            ReferencedNamespace.Types[factoryClassIndex].Members.Add(referenceClass);

            AddCastMethods(interfaceData, false, referenceClass);

            var interfaceId = GrainInterfaceData.GetGrainInterfaceId(interfaceData.Type);
            var interfaceIdMethod = new CodeMemberProperty
            {
                Name = "InterfaceId",
                Type = new CodeTypeReference(typeof (int)),
                Attributes = MemberAttributes.Family | MemberAttributes.Override,
                HasSet = false,
                HasGet = true
            };
            interfaceIdMethod.GetStatements.Add(new CodeMethodReturnStatement(new CodePrimitiveExpression(interfaceId)));
            referenceClass.Members.Add(interfaceIdMethod);


            var left = new CodeBinaryOperatorExpression(
                new CodeArgumentReferenceExpression("interfaceId"),
                CodeBinaryOperatorType.ValueEquality,
                new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "InterfaceId"));

            var interfaceList = GrainInterfaceData.GetRemoteInterfaces(interfaceData.Type);
            foreach (int iid in interfaceList.Keys)
            {
                if (iid == interfaceId) continue; // already covered the main interfaces

                left = new CodeBinaryOperatorExpression(
                    left,
                    CodeBinaryOperatorType.BooleanOr,
                    new CodeBinaryOperatorExpression(
                        new CodeArgumentReferenceExpression("interfaceId"),
                        CodeBinaryOperatorType.ValueEquality,
                        new CodePrimitiveExpression(iid)));
            }

            var interfaceIsCompatibleMethod = new CodeMemberMethod
            {
                Name = "IsCompatible",
                ReturnType = new CodeTypeReference(typeof (bool))
            };
            interfaceIsCompatibleMethod.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(int)), "interfaceId"));
            interfaceIsCompatibleMethod.Attributes = MemberAttributes.Public | MemberAttributes.Override;
            interfaceIsCompatibleMethod.Statements.Add(new CodeMethodReturnStatement(left));
            referenceClass.Members.Add(interfaceIsCompatibleMethod);

            var interfaceNameMethod = new CodeMemberProperty
            {
                Name = "InterfaceName",
                Type = new CodeTypeReference(typeof (string)),
                Attributes = MemberAttributes.Public | MemberAttributes.Override,
                HasSet = false,
                HasGet = true
            };
            interfaceNameMethod.GetStatements.Add(new CodeMethodReturnStatement(new CodePrimitiveExpression(FixupTypeName(interfaceData.TypeFullName))));
            referenceClass.Members.Add(interfaceNameMethod);

            var invokerClassName = interfaceData.InvokerClassName;

            var getMethodNameMethod = new CodeMemberMethod
            {
                Name = "GetMethodName",
                ReturnType = new CodeTypeReference(typeof (string))
            };
            getMethodNameMethod.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(int)), "interfaceId"));
            getMethodNameMethod.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(int)), "methodId"));
            getMethodNameMethod.Attributes = MemberAttributes.Family | MemberAttributes.Override;
            var methodInvokerName = string.Format("{0}.GetMethodName", FixupTypeName(invokerClassName));
            getMethodNameMethod.Statements.Add(
                new CodeMethodReturnStatement(
                    new CodeMethodInvokeExpression(
                        null, methodInvokerName, 
                        new CodeArgumentReferenceExpression("interfaceId"), 
                        new CodeArgumentReferenceExpression("methodId"))));
            referenceClass.Members.Add(getMethodNameMethod);

            CodeTypeDeclaration invokerClass = GetInvokerClass(interfaceData, true);
            invokerClass.TypeAttributes = TypeAttributes.NotPublic;
            ReferencedNamespace.Types.Add(invokerClass);

            ReferencedNamespace.Imports.Add(new CodeNamespaceImport("System"));
            ReferencedNamespace.Imports.Add(new CodeNamespaceImport("System.Net"));
            ReferencedNamespace.Imports.Add(new CodeNamespaceImport("System.Runtime.Serialization"));
            ReferencedNamespace.Imports.Add(new CodeNamespaceImport("System.Runtime.Serialization.Formatters.Binary"));
            ReferencedNamespace.Imports.Add(new CodeNamespaceImport("System.IO"));
            ReferencedNamespace.Imports.Add(new CodeNamespaceImport("System.Collections.Generic"));

            MethodInfo[] methods = GrainInterfaceData.GetMethods(interfaceData.Type);
            AddMethods(methods, referenceClass, genericTypeParam, isObserver);
        }

        /// <summary>
        /// Find the namespace of type t and the assembly file in which type t is defined
        /// Add these in lists. Later this information is used to compile grain client
        /// </summary>
        internal void RecordReferencedNamespaceAndAssembly(Type t)
        {
            RecordReferencedNamespaceAndAssembly(t, true);
        }

        internal void RecordReferencedAssembly(Type t)
        {
            RecordReferencedNamespaceAndAssembly(t, false);
        }

        protected virtual string FixupTypeName(string str)
        {
            return str;
        }

        protected virtual CodeTypeDeclaration GetStateClass(
            GrainInterfaceData grainInterfaceData,
            Action<Type> referred,
            string stateClassBaseName,
            string stateClassName,
            out bool hasStateClass)
        {
            var sourceType = grainInterfaceData.Type;

            stateClassName = FixupTypeName(stateClassName);
            CodeTypeParameterCollection genericTypeParams = grainInterfaceData.GenericTypeParams;

            Func<Type, bool> nonamespace = t => CurrentNamespace == t.Namespace || ReferencedNamespaces.Contains(t.Namespace);

            Type persistentInterface = GetPersistentInterface(sourceType);

            if (persistentInterface!=null)
            {
                if (!persistentInterface.IsInterface)
                {
                    hasStateClass = false;
                    return null;
                }
                else
                {
                    ConsoleText.WriteError(String.Format("Warning: Usage of grain state interfaces as type arguments for Grain<T> has been deprecated. " +
                        "Define an equivalent class with automatic properties instead of the state interface for {0}.", sourceType.FullName));
                }
            }

            Dictionary<string, PropertyInfo> asyncProperties = GrainInterfaceData.GetPersistentProperties(persistentInterface)
                .ToDictionary(p => p.Name.Substring(p.Name.LastIndexOf('.') + 1), p => p);

            Dictionary<string, string> properties = asyncProperties.ToDictionary(p => p.Key,
                    p => GetGenericTypeName(GrainInterfaceData.GetPromptType(p.Value.PropertyType), referred, nonamespace));

            var stateClass = new CodeTypeDeclaration(stateClassBaseName);
            if (genericTypeParams != null) 
                stateClass.TypeParameters.AddRange(genericTypeParams);
            stateClass.IsClass = true;

            if (persistentInterface != null)
                stateClass.TypeAttributes = persistentInterface.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic;
            else
                stateClass.TypeAttributes = TypeAttributes.Public;

            stateClass.BaseTypes.Add(new CodeTypeReference(typeof(GrainState), CodeTypeReferenceOptions.GlobalReference));
            MarkAsGeneratedCode(stateClass);
            referred(typeof(GrainState));

            if (persistentInterface != null)
                stateClass.BaseTypes.Add( new CodeTypeReference(GetGenericTypeName(persistentInterface, referred, nonamespace)));

            stateClass.CustomAttributes.Add(new CodeAttributeDeclaration(typeof(SerializableAttribute).Name));
            stateClass.CustomAttributes.Add(new CodeAttributeDeclaration(new CodeTypeReference(typeof(GrainStateAttribute), CodeTypeReferenceOptions.GlobalReference),
                new CodeAttributeArgument(new CodePrimitiveExpression(grainInterfaceData.Type.Namespace + "." + TypeUtils.GetParameterizedTemplateName(grainInterfaceData.Type, language: language)))));

            referred(typeof(SerializableAttribute));
            referred(typeof(OnDeserializedAttribute));

            var initStateFields = new CodeMemberMethod {Name = "InitStateFields"};
            initStateFields.Attributes = (initStateFields.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Private;
            foreach (var peoperty in asyncProperties)
            {
                Type propertyType = peoperty.Value.PropertyType;

                bool noCreateNew = propertyType.IsPrimitive || typeof(string).IsAssignableFrom(propertyType) // Primative types
                    || propertyType.IsAbstract || propertyType.IsInterface || propertyType.IsGenericParameter // No concrete implementation
                    || propertyType.GetConstructor(Type.EmptyTypes) == null; // No default constructor

                var initExpression = noCreateNew // Pre-initialize this type to default value
                    ? (CodeExpression) new CodeDefaultValueExpression( new CodeTypeReference(GetGenericTypeName(propertyType, referred, nonamespace)))
                    : new CodeObjectCreateExpression( new CodeTypeReference(GetGenericTypeName(propertyType, referred, nonamespace)));

                initStateFields.Statements.Add(new CodeAssignStatement(
                    new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), peoperty.Key),
                    initExpression));
            }

            hasStateClass = properties.Count > 0;

            if (hasStateClass)
            {
                foreach (var pair in properties)
                    GenerateStateClassProperty(stateClass, asyncProperties[pair.Key], pair.Key, pair.Value);

                var returnType = new CodeTypeReference("System.Collections.Generic.IDictionary",
                    new CodeTypeReference(typeof(string)), new CodeTypeReference(typeof(object)));
                var concreteType = new CodeTypeReference("System.Collections.Generic.Dictionary",
                    new CodeTypeReference(typeof(string)), new CodeTypeReference(typeof(object)));

                var asDictionary = new CodeMemberMethod
                {
                    Name = "AsDictionary",
                    Attributes = MemberAttributes.Public | MemberAttributes.Override,
                    ReturnType = returnType
                };

                asDictionary.Statements.Add(new CodeVariableDeclarationStatement(concreteType, "result", new CodeObjectCreateExpression(concreteType)));
                foreach (var pair in properties)
                    asDictionary.Statements.Add(new CodeAssignStatement(
                        new CodeIndexerExpression(new CodeVariableReferenceExpression("result"), new CodePrimitiveExpression(pair.Key)),
                        new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), pair.Key)));

                asDictionary.Statements.Add(new CodeMethodReturnStatement(new CodeVariableReferenceExpression("result")));
                stateClass.Members.Add(asDictionary);

                GenerateSetAll(stateClass, properties);
                GenerateToString(stateClass, stateClassName, properties);
            }

            // Copier, serializer, and deserializer for the state class
            var copier = SerializerGenerationUtilities.GenerateCopier("_Copier", stateClassName, genericTypeParams);
            var serializer = SerializerGenerationUtilities.GenerateSerializer("_Serializer", stateClassName, genericTypeParams);
            var deserializer = SerializerGenerationUtilities.GenerateDeserializer("_Deserializer", stateClassName, genericTypeParams);

            var ctor = new CodeConstructor { Attributes = (copier.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public };
            ctor.BaseConstructorArgs.Add(new CodePrimitiveExpression(TypeUtils.GetFullName(grainInterfaceData.Type, language)));
            ctor.Statements.Add(new CodeMethodInvokeExpression(
                new CodeThisReferenceExpression(),
                "InitStateFields"));

            copier.Statements.Add(new CodeMethodReturnStatement(
                new CodeMethodInvokeExpression(new CodeVariableReferenceExpression("input"), "DeepCopy")));

            serializer.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeVariableReferenceExpression("input"),
                    "SerializeTo", new CodeArgumentReferenceExpression("stream")));

            deserializer.Statements.Add(new CodeVariableDeclarationStatement(stateClassName, "result",
                new CodeObjectCreateExpression(stateClassName)));
            deserializer.Statements.Add(new CodeMethodInvokeExpression(
                    new CodeVariableReferenceExpression("result"),
                    "DeserializeFrom",
                    new CodeArgumentReferenceExpression("stream")));
            deserializer.Statements.Add(new CodeMethodReturnStatement(
                    new CodeVariableReferenceExpression("result")));

            stateClass.Members.Add(ctor);
            stateClass.Members.Add(initStateFields);
            stateClass.Members.Add(copier);
            stateClass.Members.Add(serializer);
            stateClass.Members.Add(deserializer);

            return stateClass;
        }

        protected virtual void GenerateSetAll(CodeTypeDeclaration stateClass, Dictionary<string, string> properties)
        {
            // Could be abstract if GenerateNamespace were abstract.
            throw new NotImplementedException("InvokerGeneratorBasic.GenerateSetAll()");
        }

        protected virtual void GenerateToString(CodeTypeDeclaration stateClass, string stateClassName, Dictionary<string, string> properties)
        {
            // Could be abstract if GenerateNamespace were abstract.
            throw new NotImplementedException("InvokerGeneratorBasic.GenerateToString()");
        }

        protected virtual void GenerateStateClassProperty(CodeTypeDeclaration stateClass, PropertyInfo propInfo, string name, string type)
        {
            // Could be abstract if GenerateNamespace were abstract.
            throw new NotImplementedException("InvokerGeneratorBasic.GenerateToString()");
        }

        protected virtual void AddCreateObjectReferenceMethods(GrainInterfaceData grainInterfaceData, CodeTypeDeclaration factoryClass)
        {
            throw new NotImplementedException("GrainNamespace.AddCreateObjectReferenceMethods");
        }

        protected virtual void AddGetGrainMethods(GrainInterfaceData iface, CodeTypeDeclaration factoryClass)
        {
            throw new NotImplementedException("GrainNamespace.AddGetGrainMethods");
        }
        
        /// <summary>
        /// Generate Cast method in CodeDom and add it in reference class
        /// </summary>
        /// <param name="si">The service interface this grain reference type is being generated for</param>
        /// <param name="isFactory">whether the class being generated is a factory class rather than a grainref implementation</param>
        /// <param name="referenceClass">The class being generated for this grain reference type</param>
        protected virtual void AddCastMethods(GrainInterfaceData si, bool isFactory, CodeTypeDeclaration referenceClass)
        {
            throw new NotImplementedException("GrainNamespace.AddCastMethods");
        }

        /// <summary>
        /// Generates a wrapper method that takes arguments of the original method.
        /// </summary>
        protected virtual CodeTypeMember GetBasicReferenceMethod(MethodInfo methodInfo, CodeTypeParameterCollection genericTypeParam, bool isObserver)
        {
            SerializerGenerationManager.RecordTypeToGenerate(methodInfo.ReturnType);
            foreach (var paramInfo in methodInfo.GetParameters())
                SerializerGenerationManager.RecordTypeToGenerate(paramInfo.ParameterType);

            CodeTypeReference returnType;
            if (!isObserver)
            {
                // Method is expected to return either a Task or a grain reference
                if (!GrainInterfaceData.IsTaskType(methodInfo.ReturnType) &&
                    !typeof (IAddressable).IsAssignableFrom(methodInfo.ReturnType))
                    throw new InvalidOperationException(
                        string.Format("Unsupported return type {0}. Method Name={1} Declaring Type={2}",
                            methodInfo.ReturnType.FullName, methodInfo.Name,
                            TypeUtils.GetFullName(methodInfo.DeclaringType, language)));

                returnType = CreateCodeTypeReference(methodInfo.ReturnType, language);
            }
            else
                returnType = new CodeTypeReference(typeof (void));

            var referenceMethod = new CodeMemberMethod
            {
                Name = methodInfo.Name,
                ReturnType = returnType
            };

            foreach (var param in methodInfo.GetParameters())
            {
                var paramName = GetParameterName(param);
                CodeParameterDeclarationExpression p = param.ParameterType.IsGenericType
                    ? new CodeParameterDeclarationExpression(
                        TypeUtils.GetParameterizedTemplateName(param.ParameterType, true,
                            tt => CurrentNamespace != tt.Namespace && !ReferencedNamespaces.Contains(tt.Namespace), language),
                        paramName)
                    : new CodeParameterDeclarationExpression(param.ParameterType, paramName);

                p.Direction = FieldDirection.In;
                referenceMethod.Parameters.Add(p);
            }
            
            referenceMethod.Attributes = MemberAttributes.Public | MemberAttributes.Final;
            var pit = new CodeTypeReference(GetGenericTypeName(methodInfo.DeclaringType, type => { }, t => false));
            referenceMethod.PrivateImplementationType = pit;

            var methodImpl = new CodeSnippetStatement(GetBasicMethodImpl(methodInfo));
            referenceMethod.Statements.Add(methodImpl);
            return referenceMethod;
        }

        /// <summary>
        /// Generate reference method body with original argument types
        /// </summary>
        protected virtual string GetBasicMethodImpl(MethodInfo methodInfo)
        {
            throw new NotImplementedException("GrainNamespace.GetBasicMethodImpl");
        }

        /// <summary>
        /// Generate any safeguard check statements for the generated Invoke for the specified method
        /// </summary>
        /// <param name="methodInfo">The method for which the invoke is being generated for </param>
        /// <returns></returns>
        protected virtual string GetParamGuardCheckStatements(MethodInfo methodInfo)
        {
            var paramGuardStatements = new StringBuilder();
            foreach (ParameterInfo p in methodInfo.GetParameters())
            {
                // For any parameters of type IGrainObjerver, the object passed at runtime must also be a GrainReference
                if (typeof (IGrainObserver).IsAssignableFrom(p.ParameterType))
                    paramGuardStatements.AppendLine(string.Format(@"GrainFactoryBase.CheckGrainObserverParamInternal({0});",
                        GetParameterName(p)));
            }
            return paramGuardStatements.ToString();
        }

        protected virtual string GetInvokeArguments(MethodInfo methodInfo)
        {
            throw new NotImplementedException("GrainNamespace.GetInvokeArguments");
        }

        /// <summary>
        /// Gets the name of the result type differentiating promises from normal types. For promises it returns the type of the promised value instead of the promises type itself.
        /// </summary>
        protected string GetActualMethodReturnType(Type type, SerializeFlag flag)
        {
            if (!type.IsGenericType)
                return GetGenericTypeName(type, flag);

            if (GrainInterfaceData.IsTaskType(type))
            {
                Type[] genericArguments = type.GetGenericArguments();
                if (genericArguments.Length == 1) 
                    return GetGenericTypeName(genericArguments[0], flag);

                var errorMsg = String.Format("Unexpected number of arguments {0} for generic type {1} used as a return type. Only Type<T> are supported as generic return types of grain methods.", genericArguments.Length, type);
                ReportError(errorMsg);
                throw new ApplicationException(errorMsg);
            }

            return GetGenericTypeName(type, flag);
        }

        protected CodeTypeReference GetReturnTypeReference(Type type, SerializeFlag flag)
        {
            return type == typeof(void) 
                ? new CodeTypeReference(typeof(void)) 
                : new CodeTypeReference(GetGenericTypeName(type, flag));
        }

        private static CodeTypeReference CreateCodeTypeReference(Type type, Language language)
        {
            var baseName = TypeUtils.GetSimpleTypeName(type, language: language);
            if (!type.IsGenericParameter) 
                baseName = type.Namespace + "." + baseName;

            var codeRef = new CodeTypeReference(baseName);
            if ((type.IsGenericType || type.IsGenericTypeDefinition))
                foreach (Type genericArg in type.GetGenericArguments())
                    codeRef.TypeArguments.Add(CreateCodeTypeReference(genericArg, language));

            return codeRef;
        }

        private void GetActivationNamespace(CodeNamespace factoryNamespace, GrainInterfaceData grainInterfaceData)
        {
            if (!typeof (Grain).IsAssignableFrom(grainInterfaceData.Type)) return;

            // generate a state class
            bool hasStateClass;
            var code = GetStateClass(grainInterfaceData,
                RecordReferencedNamespaceAndAssembly,
                grainInterfaceData.StateClassBaseName,
                grainInterfaceData.StateClassName,
                out hasStateClass);

            if (code != null && hasStateClass)
                factoryNamespace.Types.Add(code);
        }

        private CodeTypeDeclaration GetInvokerClass(GrainInterfaceData si, bool isClient)
        {
            Type grainType = si.Type;
            CodeTypeParameterCollection genericTypeParams = si.GenericTypeParams;

            var invokerClass = new CodeTypeDeclaration(si.InvokerClassBaseName);

            if (genericTypeParams != null)
                invokerClass.TypeParameters.AddRange(genericTypeParams);

            invokerClass.IsClass = true;
            MarkAsGeneratedCode(invokerClass);
            invokerClass.BaseTypes.Add(si.IsExtension
                ? new CodeTypeReference(typeof (IGrainExtensionMethodInvoker), CodeTypeReferenceOptions.GlobalReference)
                : new CodeTypeReference(typeof (IGrainMethodInvoker), CodeTypeReferenceOptions.GlobalReference));

            GrainInterfaceInfo grainInterfaceInfo = GetInterfaceInfo(grainType);
            var interfaceId = grainInterfaceInfo.Interfaces.Keys.First();
            invokerClass.CustomAttributes.Add(new CodeAttributeDeclaration( new CodeTypeReference(typeof(MethodInvokerAttribute), CodeTypeReferenceOptions.GlobalReference),
                new CodeAttributeArgument(new CodePrimitiveExpression(grainType.Namespace + "." + TypeUtils.GetParameterizedTemplateName(grainType, language: language))),
                new CodeAttributeArgument(new CodePrimitiveExpression(interfaceId))));

            var interfaceIdProperty = new CodeMemberProperty
            {
                Name = "InterfaceId",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                Type = new CodeTypeReference(typeof (int))
            };
            interfaceIdProperty.GetStatements.Add(new CodeMethodReturnStatement(new CodePrimitiveExpression(interfaceId)));
            interfaceIdProperty.PrivateImplementationType = new CodeTypeReference(typeof(IGrainMethodInvoker), CodeTypeReferenceOptions.GlobalReference);
            invokerClass.Members.Add(interfaceIdProperty);

            // Add invoke method for Orleans message 
            var orleansInvoker = new CodeMemberMethod
            {
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                Name = "Invoke",
                ReturnType = new CodeTypeReference(typeof (Task<object>), CodeTypeReferenceOptions.GlobalReference)
            };
            orleansInvoker.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(IAddressable), CodeTypeReferenceOptions.GlobalReference), "grain"));
            orleansInvoker.Parameters.Add(new CodeParameterDeclarationExpression(typeof(int), "interfaceId"));
            orleansInvoker.Parameters.Add(new CodeParameterDeclarationExpression(typeof(int), "methodId"));
            orleansInvoker.Parameters.Add(new CodeParameterDeclarationExpression(typeof(object[]), "arguments"));
            orleansInvoker.PrivateImplementationType = new CodeTypeReference(typeof(IGrainMethodInvoker), CodeTypeReferenceOptions.GlobalReference);

            var orleansInvokerImpl = new CodeSnippetStatement(GetInvokerImpl(si, invokerClass, grainType, grainInterfaceInfo, isClient));
            orleansInvoker.Statements.Add(orleansInvokerImpl);
            invokerClass.Members.Add(orleansInvoker);

            // Add TryInvoke method for Orleans message, if the type is an extension interface
            if (si.IsExtension)
            {
                var orleansTryInvoker = new CodeMemberMethod
                {
                    Attributes = MemberAttributes.Public | MemberAttributes.Final,
                    Name = "Invoke",
                    ReturnType = new CodeTypeReference(typeof (Task<object>), CodeTypeReferenceOptions.GlobalReference)
                };
                orleansTryInvoker.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(IGrainExtension), CodeTypeReferenceOptions.GlobalReference), "grain"));
                orleansTryInvoker.Parameters.Add(new CodeParameterDeclarationExpression(typeof(int), "interfaceId"));
                orleansTryInvoker.Parameters.Add(new CodeParameterDeclarationExpression(typeof(int), "methodId"));
                orleansTryInvoker.Parameters.Add(new CodeParameterDeclarationExpression(typeof(object[]), "arguments"));
                orleansTryInvoker.PrivateImplementationType = new CodeTypeReference(typeof(IGrainExtensionMethodInvoker), CodeTypeReferenceOptions.GlobalReference);

                var orleansTryInvokerImp = new CodeSnippetStatement(GetInvokerImpl(si, invokerClass, grainType, grainInterfaceInfo, isClient));
                orleansTryInvoker.Statements.Add(orleansTryInvokerImp);
                invokerClass.Members.Add(orleansTryInvoker);
            }

            // Add GetMethodName() method 
            var getMethodName = new CodeMemberMethod
            {
                Attributes = MemberAttributes.Public | MemberAttributes.Final | MemberAttributes.Static,
                Name = "GetMethodName",
                ReturnType = new CodeTypeReference(typeof (string))
            };
            getMethodName.Parameters.Add(new CodeParameterDeclarationExpression(typeof(int), "interfaceId"));
            getMethodName.Parameters.Add(new CodeParameterDeclarationExpression(typeof(int), "methodId"));

            var orleansGetMethodNameImpl = new CodeSnippetStatement(GetOrleansGetMethodNameImpl(grainType, grainInterfaceInfo));
            getMethodName.Statements.Add(orleansGetMethodNameImpl);
            invokerClass.Members.Add(getMethodName);
            return invokerClass;
        }

        private static bool ShouldGenerateObjectRefFactory(GrainInterfaceData ifaceData)
        {
            var ifaceType = ifaceData.Type;
            // generate CreateObjectReference in 2 cases:
            // 1) for interfaces derived from IGrainObserver
            // 2) when specifically specifies via FactoryTypes.ClientObject or FactoryTypes.Both 
           if(IsObserver(ifaceType))
                return true;

            var factoryType = FactoryAttribute.CollectFactoryTypesSpecified(ifaceType);
            return factoryType == FactoryAttribute.FactoryTypes.ClientObject || factoryType == FactoryAttribute.FactoryTypes.Both;
        }

        private static bool IsObserver(Type type)
        {
            return typeof (IGrainObserver).IsAssignableFrom(type);
        }

        private void AddMethods(MethodInfo[] methods, CodeTypeDeclaration referenceClass, CodeTypeParameterCollection genericTypeParam, bool isObserver)
        {
            methodIdCollisionDetection.Clear();
            if (methods == null || methods.Length <= 0) return;

            foreach (var methodInfo in methods)
                AddMethod(methodInfo, referenceClass, genericTypeParam, isObserver);
        }

        private void AddMethod(MethodInfo methodInfo, CodeTypeDeclaration referenceClass, CodeTypeParameterCollection genericTypeParam, bool isObserver)
        {
            if (methodInfo.IsStatic || IsSpecialEventMethod(methodInfo))
                return; // skip such methods            

            int methodId = GrainInterfaceData.ComputeMethodId(methodInfo);
            if (methodIdCollisionDetection.Contains(methodId))
            {
                ReportError(string.Format("Collision detected for method {0}, declaring type {1}, consider renaming method name",
                    methodInfo.Name, methodInfo.DeclaringType.FullName));
            }
            else
            {
                var code = GetBasicReferenceMethod(methodInfo, genericTypeParam, isObserver);
                referenceClass.Members.Add(code); // method with original argument types
                methodIdCollisionDetection.Add(methodId);
            }

            if (typeof (IAddressable).IsAssignableFrom(methodInfo.ReturnType))
                RecordReferencedNamespaceAndAssembly(methodInfo.ReturnType);
        }

        #region utility methods

        /// <summary>
        /// Makes errors visible in VS and MSBuild by prefixing error message with "Error"
        /// </summary>
        /// <param name="errorMsg">Error message</param>
        internal static void ReportError(string errorMsg)
        {
            ConsoleText.WriteError("Error: Orleans code generator found error: " + errorMsg);
        }

        /// <summary>
        /// Makes errors visible in VS and MSBuild by prefixing error message with "Error"
        /// </summary>
        /// <param name="errorMsg">Error message</param>
        /// <param name="exc">Exception associated with the error</param>
        internal static void ReportError(string errorMsg, Exception exc)
        {
            ConsoleText.WriteError("Error: Orleans code generator found error: " + errorMsg, exc);
        }

        /// <summary>
        /// Makes warnings visible in VS and MSBuild by prefixing error message with "Warning"
        /// </summary>
        /// <param name="warning">Warning message</param>
        internal static void ReportWarning(string warning)
        {
            ConsoleText.WriteWarning("Warning: " + warning);
        }
        

        private void AddFactoryMethods(GrainInterfaceData si, CodeTypeDeclaration factoryClass)
        {
            RecordReferencedNamespaceAndAssembly(si.Type);
            if (GrainInterfaceData.IsGrainType(si.Type) && ShouldGenerateGetGrainMethods(si.Type))
                AddGetGrainMethods(si, factoryClass);
        }

        private static bool ShouldGenerateGetGrainMethods(Type type)
        {
            // we don't generate these methods if this is a client object factory.
            var factoryType = FactoryAttribute.CollectFactoryTypesSpecified(type);
            return factoryType != FactoryAttribute.FactoryTypes.ClientObject;
        }
        
        /// <summary>
        /// Decide whether this grain method is declared in this grain dll file
        /// </summary>
        private bool IsDeclaredHere(Type type)
        {
            return type.Assembly.Equals(grainAssembly);
        }

        private void RecordReferencedNamespaceAndAssembly(Type t, bool addNamespace)
        {
            RecordReferencedNamespaceAndAssembly(addNamespace ? t.Namespace : null, t.Assembly.GetName().Name);
            var indirect = t.GetInterfaces().ToList();

            for (var parent = t.BaseType; typeof (Grain).IsAssignableFrom(parent); parent = parent.BaseType)
                indirect.Add(parent);
            foreach (var t2 in indirect)
                RecordReferencedNamespaceAndAssembly(addNamespace ? t2.Namespace : null, t2.Assembly.GetName().Name);
        }

        private void RecordReferencedNamespaceAndAssembly(string nspace, string assembly)
        {
            if (!String.IsNullOrEmpty((nspace)))
                if (!ReferencedNamespaces.Contains(nspace) && ReferencedNamespace.Name != nspace)
                    ReferencedNamespaces.Add(nspace);

            if (!ReferencedAssemblies.Contains(assembly) && grainAssembly.GetName().Name + "Client" != assembly)
                ReferencedAssemblies.Add(assembly);
        }

        /// <summary>
        /// Get the name string for generic type
        /// </summary>
        private string GetGenericTypeName(Type type, SerializeFlag flag = SerializeFlag.NoSerialize, bool includeImpl = true)
        {
            return GetGenericTypeName(type, RecordReferencedNamespaceAndAssembly,
                t => flag != SerializeFlag.NoSerialize && IsDeclaredHere(type) && includeImpl);
        }

        #endregion
    }
}
