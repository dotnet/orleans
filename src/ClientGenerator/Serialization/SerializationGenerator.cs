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
using System.Collections.Generic;
using System.CodeDom;
using System.Linq;
using System.Reflection;

using Orleans.Runtime;
using Orleans.Serialization;

// Suppress ReSharper warnings about use of CodeDom *Attributes enum's which should have [Flags] attribute but don't
// ReSharper disable BitwiseOperatorOnEnumWihtoutFlags

namespace Orleans.CodeGeneration.Serialization
{
    internal static class SerializationGenerator
    {
        delegate CodeStatementCollection SetterGenerator(CodeExpression value);

        const string SERIALIZER_CLASS_NAME_SUFFIX = "Serialization";

        /// <summary>
        /// Generate all the necessary logic for serialization of payload types used by grain interfaces.
        /// </summary>
        internal static void GenerateSerializationForClass(Assembly grainAssembly, Type t, CodeNamespace container, HashSet<string> referencedNamespaces, Language language)
        {
            var generateSerializers = !CheckForCustomSerialization(t);
            var generateCopier = !CheckForCustomCopier(t);

            if (!generateSerializers && !generateCopier)
                return; // If the class declares all custom implementations, then we don't need to do anything...

            bool notVB = (language != Language.VisualBasic);
            var openGenerics = notVB ? "<" : "(Of ";
            var closeGenerics = notVB ? ">" : ")";

            // Add the class's namespace to this namespace's imports, as well as some other imports we use
            container.Imports.Add(new CodeNamespaceImport("System"));
            container.Imports.Add(new CodeNamespaceImport("System.Collections.Generic"));
            container.Imports.Add(new CodeNamespaceImport("System.Reflection"));
            container.Imports.Add(new CodeNamespaceImport("Orleans.Serialization"));
            if (!String.IsNullOrEmpty(t.Namespace))
            {
                container.Imports.Add(new CodeNamespaceImport(t.Namespace));
            }

            // Create the class declaration, including any required generic parameters
            // At one time this was a struct, not a class, so all the variable names are "structFoo". Too bad.
            // Note that we need to replace any periods in the type name with _ to properly handle nested classes
            var className = TypeUtils.GetSimpleTypeName(TypeUtils.GetFullName(t));
            var serializationClassName = className.Replace('.', '_') + SERIALIZER_CLASS_NAME_SUFFIX;
            var serializationClassOpenName = serializationClassName;
            var classDecl = new CodeTypeDeclaration(serializationClassName) {IsClass = true};
            classDecl.Attributes = (classDecl.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            classDecl.TypeAttributes = TypeAttributes.NotPublic;
            CodeGeneratorBase.MarkAsGeneratedCode(classDecl);

            if (!t.IsGenericType)
                classDecl.CustomAttributes.Add(
                    new CodeAttributeDeclaration(new CodeTypeReference(typeof (RegisterSerializerAttribute),
                        CodeTypeReferenceOptions.GlobalReference)));

            if (t.IsGenericType)
            {
                className += openGenerics;
                serializationClassOpenName += openGenerics;
                bool first = true;
                foreach (var genericParameter in t.GetGenericTypeDefinition().GetGenericArguments())
                {
                    var param = new CodeTypeParameter(genericParameter.Name);
                    if ((genericParameter.GenericParameterAttributes & GenericParameterAttributes.ReferenceTypeConstraint) != GenericParameterAttributes.None)
                        param.Constraints.Add(" class");
                    if ((genericParameter.GenericParameterAttributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != GenericParameterAttributes.None)
                        param.Constraints.Add(" struct");

                    var constraints = genericParameter.GetGenericParameterConstraints();
                    foreach (var constraintType in constraints)
                        param.Constraints.Add( new CodeTypeReference(TypeUtils.GetParameterizedTemplateName(constraintType)));

                    if ((genericParameter.GenericParameterAttributes & GenericParameterAttributes.DefaultConstructorConstraint) != GenericParameterAttributes.None)
                        param.HasConstructorConstraint = true;

                    classDecl.TypeParameters.Add(param);
                    if (!first)
                    {
                        className += ", ";
                        serializationClassOpenName += ",";
                    }
                    className += genericParameter.Name;
                    first = false;
                }
                className += closeGenerics;
                serializationClassOpenName += closeGenerics;
            }

            // A couple of repeatedly-used CodeDom snippets
            var classType = new CodeTypeOfExpression(className);
            var classTypeReference = new CodeTypeReference(className);
            var objectTypeReference = new CodeTypeReference(typeof(object));
            var serMgrRefExp = new CodeTypeReferenceExpression(typeof(SerializationManager));
            var currentSerialzationContext = new CodePropertyReferenceExpression(new CodeTypeReferenceExpression(typeof(SerializationContext)), "Current");

            // Static DeepCopyInner method:
            var copier = new CodeMemberMethod();
            if (generateCopier)
                classDecl.Members.Add(copier);

            copier.Attributes = (copier.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            copier.Attributes = (copier.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            copier.Name = "DeepCopier";
            copier.Parameters.Add(new CodeParameterDeclarationExpression(objectTypeReference, "original"));
            bool shallowCopyable = t.IsOrleansShallowCopyable();

            if (shallowCopyable)
                copier.Statements.Add(new CodeMethodReturnStatement(new CodeArgumentReferenceExpression("original")));
            else
                copier.Statements.Add(new CodeVariableDeclarationStatement(classTypeReference, "input",
                    new CodeCastExpression(classTypeReference, new CodeArgumentReferenceExpression("original"))));

            copier.ReturnType = objectTypeReference;

            // Static serializer method:
            var serializer = new CodeMemberMethod();
            if (generateSerializers)
                classDecl.Members.Add(serializer);

            serializer.Attributes = (serializer.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            serializer.Attributes = (serializer.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            serializer.Name = "Serializer";
            serializer.Parameters.Add(new CodeParameterDeclarationExpression(objectTypeReference, "untypedInput"));
            serializer.Parameters.Add(new CodeParameterDeclarationExpression(typeof(BinaryTokenStreamWriter), "stream"));
            serializer.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Type), "expected"));
            serializer.ReturnType = new CodeTypeReference(typeof(void));
            serializer.Statements.Add(new CodeVariableDeclarationStatement(classTypeReference, "input",
                new CodeCastExpression(classTypeReference, new CodeArgumentReferenceExpression("untypedInput"))));

            // Static deserializer method; note that this will never get called for null values or back references
            var deserializer = new CodeMemberMethod();
            if (generateSerializers)
                classDecl.Members.Add(deserializer);

            deserializer.Attributes = (deserializer.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            deserializer.Attributes = (deserializer.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            deserializer.Name = "Deserializer";
            deserializer.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Type), "expected"));
            deserializer.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(BinaryTokenStreamReader),CodeTypeReferenceOptions.GlobalReference), "stream"));
            deserializer.ReturnType = objectTypeReference;

            // Static constructor, which just calls the Init method
            var staticConstructor = new CodeTypeConstructor();
            classDecl.Members.Add(staticConstructor);
            staticConstructor.Statements.Add(new CodeMethodInvokeExpression(new CodeMethodReferenceExpression(null, "Register")));

            // Init method, which registers the type with the serialization manager, and later may get some static FieldInfo initializers
            var init = new CodeMemberMethod();
            classDecl.Members.Add(init);
            init.Name = "Register";
            init.Attributes = (init.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            init.Attributes = (init.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;

            if (generateCopier && generateSerializers)
            {
                init.Statements.Add(new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(new CodeTypeReference(typeof(SerializationManager), CodeTypeReferenceOptions.GlobalReference)), "Register", classType,
                    new CodeMethodReferenceExpression(null, notVB ? "DeepCopier" : "AddressOf DeepCopier"),
                    new CodeMethodReferenceExpression(null, notVB ? "Serializer" : "AddressOf Serializer"),
                    new CodeMethodReferenceExpression(null, notVB ? "Deserializer" : "AddressOf Deserializer")));
            }
            else if (generateCopier)
            {
                init.Statements.Add(new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(new CodeTypeReference(typeof(SerializationManager), CodeTypeReferenceOptions.GlobalReference)), "Register", classType,
                    new CodeMethodReferenceExpression(null, notVB ? "DeepCopier" : "AddressOf DeepCopier"),
                    null,
                    null));
            }
            else
            {
                init.Statements.Add(new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(new CodeTypeReference(typeof(SerializationManager), CodeTypeReferenceOptions.GlobalReference)), "Register", classType,
                    null,
                    new CodeMethodReferenceExpression(null, notVB ? "Serializer" : "AddressOf Serializer"),
                    new CodeMethodReferenceExpression(null, notVB ? "Deserializer" : "AddressOf Deserializer")));
            }

            CodeStatement constructor;
            var consInfo = t.GetConstructor(Type.EmptyTypes);
            if (consInfo != null)
            {
                if (!t.ContainsGenericParameters)
                {
                    constructor = new CodeVariableDeclarationStatement(classTypeReference, "result", new CodeObjectCreateExpression(t));
                }
                else
                {
                    var typeName = TypeUtils.GetParameterizedTemplateName(t, tt => tt.Namespace != container.Name && !referencedNamespaces.Contains(tt.Namespace), true);
                    if (language == Language.VisualBasic)
                        typeName = typeName.Replace("<", "(Of ").Replace(">", ")");
                    constructor = new CodeVariableDeclarationStatement(classTypeReference, "result", 
                        new CodeObjectCreateExpression(typeName));
                }
            }
            else if (t.IsValueType)
            {
                constructor = !t.ContainsGenericParameters 
                    ? new CodeVariableDeclarationStatement(classTypeReference, "result", new CodeDefaultValueExpression(new CodeTypeReference(t))) 
                    : new CodeVariableDeclarationStatement(classTypeReference, "result", new CodeDefaultValueExpression(new CodeTypeReference(TypeUtils.GetTemplatedName(t))));
                }
                else
                {
                if (!t.ContainsGenericParameters)
                {
                    constructor = new CodeVariableDeclarationStatement(classTypeReference, "result",
                        new CodeCastExpression(className, new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(typeof(System.Runtime.Serialization.FormatterServices)),
                            "GetUninitializedObject", new CodeTypeOfExpression(t))));
                }
                else
                {
                    constructor = new CodeVariableDeclarationStatement(classTypeReference, "result",
                        new CodeCastExpression(className, new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(typeof(System.Runtime.Serialization.FormatterServices)),
                            "GetUninitializedObject", new CodeTypeOfExpression(TypeUtils.GetTemplatedName(t)))));
                }
            }
            if (!shallowCopyable)
            {
                copier.Statements.Add(constructor);
                copier.Statements.Add(new CodeMethodInvokeExpression(currentSerialzationContext, "RecordObject",
                                                                     new CodeVariableReferenceExpression("original"),
                                                                     new CodeVariableReferenceExpression("result")));
            }
            deserializer.Statements.Add(constructor);

            // For structs, once we encounter a field that we have to use reflection to set, we need to switch to a boxed representation and reflection
            // for the rest of the fields in the struct while setting. This flag indicates that we're in that mode.
            bool usingBoxedReflection = false;

            // For every field in the class:
            int counter = 0;
            List<FieldInfo> fields = GetAllFields(t).ToList();
            fields.Sort(new FieldNameComparer());
            foreach (var fld in fields)
            {
                if (fld.IsNotSerialized || fld.IsLiteral)
                    continue;

                var fldType = fld.FieldType;
                if (TypeUtilities.IsTypeIsInaccessibleForSerialization(fldType, t.Module, grainAssembly))
                {
                    ConsoleText.WriteStatus("Skipping generation of serializer for {0} because one of it's field {1} is of a private/internal type.", t.FullName, fld.Name);
                    return; // We cannot deserialize a class with a field of non-public type. Need to add a proper reporting here.
                }

                // Import the namespace for the field's type (and any of its parameters), just in case it's not already added
                ImportFieldNamespaces(fld.FieldType, container.Imports);
                SerializerGenerationManager.RecordTypeToGenerate(fld.FieldType);
                counter++;

                // Add the statements moving to and from a class instance, to the instance creation method and the non-default constructor
                // Getter and setter for this field's value from a class object
                CodeExpression getter = null;
                SetterGenerator setter = null;

                var name = fld.Name;
                // Normalize the field name -- strip trailing @ (F#) and look for automatic properties
                var normalizedName = name.TrimEnd('@');
                if (name.StartsWith("<"))
                {
                    // Backing field for an automatic property; see if it's public so we can use it
                    var propertyName = name.Substring(1, name.IndexOf('>') - 1).TrimEnd('@');
                    var property = t.GetProperty(propertyName);
                    // If the property is public and not hidden...
                    if ((property != null) && property.DeclaringType == fld.DeclaringType)
                    {
                        if (property.GetGetMethod() != null)
                            getter = new CodePropertyReferenceExpression(new CodeArgumentReferenceExpression("input"), 
                                propertyName);
                        if (!usingBoxedReflection && (property.GetSetMethod() != null))
                        {
                            setter = value =>
                                {
                                    var s = new CodeAssignStatement
                                                {
                                    Left = new CodePropertyReferenceExpression(new CodeVariableReferenceExpression("result"), propertyName),
                                                    Right = value
                                                };
                                    return new CodeStatementCollection(new CodeStatement[] { s });
                                };
                        }
                    }
                }

                var typeName = TypeUtils.GetTemplatedName(fld.FieldType, _ => !_.IsGenericParameter, language);

                // See if it's a public field
                if ((getter == null) || (setter == null))
                {
                    if (fld.Attributes.HasFlag(FieldAttributes.Public))
                    {
                        if (getter == null)
                            getter = new CodeFieldReferenceExpression(new CodeArgumentReferenceExpression("input"), normalizedName);

                        if (!usingBoxedReflection && (setter == null) && !fld.IsInitOnly)
                        {
                            setter = value =>
                                {
                                    var s = new CodeAssignStatement
                                                {
                                    Left = new CodeFieldReferenceExpression( new CodeVariableReferenceExpression("result"), normalizedName),
                                                    Right = value
                                                };
                                    return new CodeStatementCollection(new CodeStatement[] { s });
                                };
                        }
                    }
                }

                // Have to use reflection
                if ((getter == null) || (setter == null))
                {
                    // Add a static field for the FieldInfo, and a static constructor
                    string infoName = "fieldInfo" + counter;
                    var info = new CodeMemberField(typeof(FieldInfo), infoName);
                    info.Attributes |= MemberAttributes.Private | MemberAttributes.Static;
                    classDecl.Members.Add(info);
                    CodeTypeOfExpression fieldAccessType;
                    if (fld.DeclaringType == t)
                    {
                        fieldAccessType = classType;
                    }
                    else
                    {
                        FieldInfo fld2 = t.GetField(fld.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if ((fld2 != null) && fld2.DeclaringType == fld.DeclaringType)
                            fieldAccessType = classType;
                        else
                            fieldAccessType = fld.DeclaringType.IsGenericType
                                ? new CodeTypeOfExpression(TypeUtils.GetTemplatedName(fld.DeclaringType))
                                : new CodeTypeOfExpression(fld.DeclaringType);
                    }

                    init.Statements.Add(new CodeAssignStatement(new CodeFieldReferenceExpression(null, infoName),
                        new CodeMethodInvokeExpression(fieldAccessType, "GetField", new CodePrimitiveExpression(name),
                            new CodeBinaryOperatorExpression(new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(typeof(BindingFlags)), "Instance"),
                                CodeBinaryOperatorType.BitwiseOr,
                                new CodeBinaryOperatorExpression(new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(typeof(BindingFlags)), "Public"),
                                    CodeBinaryOperatorType.BitwiseOr,
                                    new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(typeof(BindingFlags)), "NonPublic"))))));

                    // Build the getter and setter
                    if (getter == null)
                        getter = new CodeMethodInvokeExpression( 
                            new CodeMethodReferenceExpression(
                                new CodeFieldReferenceExpression(null, infoName), "GetValue"), 
                            new CodeArgumentReferenceExpression("input"));

                    if (setter == null)
                    {
                        // If the type is a struct, then the setter becomes somewhat more complicated, so first treat non-structs
                        if (t.IsByRef)
                        {
                            setter = value =>
                            {
                                var s = new CodeExpressionStatement
                                {
                                    Expression =
                                        new CodeMethodInvokeExpression(
                                        new CodeMethodReferenceExpression(
                                            new CodeFieldReferenceExpression(null, infoName), "SetValue"),
                                        new CodeVariableReferenceExpression("result"), value)
                                };
                                return new CodeStatementCollection(new CodeStatement[] { s });
                            };
                        }
                        else
                        {
                            // If this is the first field to use setting by reflection in a struct, we need to box the struct before we can continue
                            if (!usingBoxedReflection)
                            {
                                usingBoxedReflection = true;
                                // NOTE: object objResult = (object)result;
                                if (!shallowCopyable)
                                    copier.Statements.Add(new CodeVariableDeclarationStatement(typeof (object), "objResult",
                                        new CodeCastExpression(typeof (object), new CodeVariableReferenceExpression("result"))));

                                deserializer.Statements.Add(new CodeVariableDeclarationStatement(typeof(object), "objResult",
                                    new CodeCastExpression(typeof(object), new CodeVariableReferenceExpression("result"))));
                            }
                            var temp = "temp" + counter;
                            setter = value =>
                                         {
                                             var s1 = new CodeVariableDeclarationStatement(typeof(object), temp, value);
                                             var s2 = new CodeExpressionStatement
                                                {
                                    Expression = new CodeMethodInvokeExpression(
                                        new CodeMethodReferenceExpression(new CodeFieldReferenceExpression(null, infoName), "SetValue"),
                                                            new CodeVariableReferenceExpression("objResult"),
                                                            new CodeVariableReferenceExpression(temp))
                                                };
                                             return new CodeStatementCollection(new CodeStatement[] { s1, s2 });
                                         };
                        }
                    }
                }

                // Copy this field, if needed
                if (!shallowCopyable)
                {
                    if (fld.FieldType.IsOrleansShallowCopyable())
                        copier.Statements.AddRange(setter(getter));
                    else
                        copier.Statements.AddRange(fld.FieldType == typeof (object)
                            ? setter(new CodeMethodInvokeExpression(serMgrRefExp, "DeepCopyInner", getter))
                            : setter(new CodeCastExpression(typeName,
                                new CodeMethodInvokeExpression(serMgrRefExp, "DeepCopyInner", getter))));
                }

                // Serialize this field
                serializer.Statements.Add(new CodeMethodInvokeExpression(serMgrRefExp, "SerializeInner", getter, new CodeArgumentReferenceExpression("stream"),
                    new CodeTypeOfExpression(typeName)));

                // Deserialize this field
                deserializer.Statements.AddRange(setter(new CodeCastExpression(typeName,
                    new CodeMethodInvokeExpression(serMgrRefExp, "DeserializeInner", 
                        new CodeTypeOfExpression(typeName), new CodeArgumentReferenceExpression("stream")))));
            }

            // Add return statements, as needed
            if (!shallowCopyable)
                copier.Statements.Add(new CodeMethodReturnStatement(new CodeVariableReferenceExpression(usingBoxedReflection ? "objResult" : "result")));

            deserializer.Statements.Add(new CodeMethodReturnStatement(new CodeVariableReferenceExpression(usingBoxedReflection ? "objResult" : "result")));

            // Special processing for generic types, necessary so that the appropriate closed types will get generated at run-time
            if (t.IsGenericType)
            {
                var masterClassName = TypeUtils.GetSimpleTypeName(t) + "GenericMaster";

                var masterClass = new CodeTypeDeclaration(masterClassName);
                container.Types.Add(masterClass);
                masterClass.IsClass = true;
                masterClass.Attributes |= MemberAttributes.Static | MemberAttributes.Assembly | MemberAttributes.Final;
                masterClass.TypeAttributes = TypeAttributes.NotPublic;
                masterClass.CustomAttributes.Add(new CodeAttributeDeclaration(new CodeTypeReference(typeof(RegisterSerializerAttribute), CodeTypeReferenceOptions.GlobalReference)));

                var masterInit = AddInitMethod(masterClass);
                masterInit.Statements.Add(new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(new CodeTypeReference(typeof(SerializationManager), CodeTypeReferenceOptions.GlobalReference)), "Register",
                    new CodeTypeOfExpression(t),
                    new CodeMethodReferenceExpression(new CodeTypeReferenceExpression(notVB ? masterClassName : "AddressOf " + masterClassName), "GenericCopier"),
                    new CodeMethodReferenceExpression(new CodeTypeReferenceExpression(notVB ? masterClassName : "AddressOf " + masterClassName), "GenericSerializer"),
                    new CodeMethodReferenceExpression(new CodeTypeReferenceExpression(notVB ? masterClassName : "AddressOf " + masterClassName), "GenericDeserializer")));

                var initClosed = new CodeMethodInvokeExpression
                                     {
                                         Method = new CodeMethodReferenceExpression
                                                      {
                                                          MethodName = "Invoke",
                                                          TargetObject =
                                                              new CodeMethodInvokeExpression(
                                                              new CodeVariableReferenceExpression
                                                                  ("closed"), "GetMethod",
                                                              new CodePrimitiveExpression(
                                                                  "Register"))
                                                      }
                                     };
                initClosed.Parameters.Add(new CodePrimitiveExpression(null));
                initClosed.Parameters.Add(new CodeArrayCreateExpression(typeof(object), 0));

                var create = new CodeMemberMethod();
                masterClass.Members.Add(create);
                create.Attributes = (create.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
                create.Attributes = (create.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
                create.Name = "CreateConcreteType";
                create.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Type[]), "typeParams"));
                create.ReturnType = new CodeTypeReference(typeof(Type));
                create.Statements.Add(new CodeMethodReturnStatement(new CodeMethodInvokeExpression(new CodeTypeOfExpression(serializationClassOpenName),
                    "MakeGenericType", new CodeArgumentReferenceExpression("typeParams"))));

                var cop = new CodeMemberMethod();
                masterClass.Members.Add(cop);
                cop.Attributes = (cop.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
                cop.Attributes = (cop.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
                cop.Name = "GenericCopier";
                cop.Parameters.Add(new CodeParameterDeclarationExpression(typeof(object), "obj"));
                cop.ReturnType = new CodeTypeReference(typeof(object));
                cop.Statements.Add(new CodeVariableDeclarationStatement(typeof(Type), "t",
                    new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(masterClassName), "CreateConcreteType",
                        new CodeMethodInvokeExpression(new CodeMethodInvokeExpression(new CodeArgumentReferenceExpression("obj"), "GetType"), "GetGenericArguments"))));
                cop.Statements.Add(new CodeVariableDeclarationStatement(typeof(MethodInfo), "f",
                    new CodeMethodInvokeExpression(new CodeVariableReferenceExpression("t"), "GetMethod", new CodePrimitiveExpression("DeepCopier"))));
                cop.Statements.Add(new CodeVariableDeclarationStatement(typeof(object[]), "args",
                    new CodeArrayCreateExpression(typeof(object), new CodeExpression[] { new CodeArgumentReferenceExpression("obj") })));
                cop.Statements.Add(new CodeMethodReturnStatement(
                    new CodeMethodInvokeExpression(new CodeVariableReferenceExpression("f"), "Invoke", new CodePrimitiveExpression(),
                        new CodeVariableReferenceExpression("args"))));

                var ser = new CodeMemberMethod();
                masterClass.Members.Add(ser);
                ser.Attributes = (ser.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
                ser.Attributes = (ser.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
                ser.Name = "GenericSerializer";
                ser.Parameters.Add(new CodeParameterDeclarationExpression(typeof(object), "input"));
                ser.Parameters.Add(new CodeParameterDeclarationExpression(typeof(BinaryTokenStreamWriter), "stream"));
                ser.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Type), "expected"));
                ser.ReturnType = new CodeTypeReference(typeof(void));
                ser.Statements.Add(new CodeVariableDeclarationStatement(typeof(Type), "t",
                    new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(masterClassName), "CreateConcreteType",
                        new CodeMethodInvokeExpression(new CodeMethodInvokeExpression(new CodeArgumentReferenceExpression("input"), "GetType"), "GetGenericArguments"))));
                ser.Statements.Add(new CodeVariableDeclarationStatement(typeof(MethodInfo), "f",
                    new CodeMethodInvokeExpression(new CodeVariableReferenceExpression("t"), "GetMethod", new CodePrimitiveExpression("Serializer"))));
                ser.Statements.Add(new CodeVariableDeclarationStatement(typeof(object[]), "args",
                    new CodeArrayCreateExpression(typeof(object), new CodeExpression[] { new CodeArgumentReferenceExpression("input"), 
                        new CodeArgumentReferenceExpression("stream"), new CodeArgumentReferenceExpression("expected")})));
                ser.Statements.Add(new CodeMethodInvokeExpression(new CodeVariableReferenceExpression("f"), "Invoke", new CodePrimitiveExpression(),
                        new CodeVariableReferenceExpression("args")));

                var deser = new CodeMemberMethod();
                masterClass.Members.Add(deser);
                deser.Attributes = (deser.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
                deser.Attributes = (deser.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
                deser.Name = "GenericDeserializer";
                deser.ReturnType = new CodeTypeReference(typeof(object));
                deser.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Type), "expected"));
                deser.Parameters.Add(new CodeParameterDeclarationExpression(typeof(BinaryTokenStreamReader), "stream"));
                deser.ReturnType = new CodeTypeReference(typeof(object));
                deser.Statements.Add(new CodeVariableDeclarationStatement(typeof(Type), "t",
                    new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(masterClassName), "CreateConcreteType",
                        new CodeMethodInvokeExpression(new CodeArgumentReferenceExpression("expected"), "GetGenericArguments"))));
                deser.Statements.Add(new CodeVariableDeclarationStatement(typeof(MethodInfo), "f",
                    new CodeMethodInvokeExpression(new CodeVariableReferenceExpression("t"), "GetMethod", new CodePrimitiveExpression("Deserializer"))));
                deser.Statements.Add(new CodeVariableDeclarationStatement(typeof(object[]), "args",
                    new CodeArrayCreateExpression(typeof(object), new CodeExpression[] { new CodeArgumentReferenceExpression("expected"), 
                        new CodeArgumentReferenceExpression("stream")})));
                deser.Statements.Add(new CodeMethodReturnStatement(
                    new CodeMethodInvokeExpression(new CodeVariableReferenceExpression("f"), "Invoke", new CodePrimitiveExpression(),
                        new CodeVariableReferenceExpression("args"))));
            }
            container.Types.Add(classDecl);
        }

        private static CodeMemberMethod AddInitMethod(CodeTypeDeclaration type)
        {
            var init = new CodeMemberMethod();
            type.Members.Add(init);
            init.Name = "Register";
            init.Attributes = (init.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            init.Attributes = (init.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            return init;
        }

        private static IEnumerable<FieldInfo> GetAllFields(Type t)
        {
            Type current = t;
            while ((current != typeof(object)) && (current != null))
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return field;
                current = current.BaseType;
            }
        }

        private class FieldNameComparer : IComparer<FieldInfo>
        {
            public int Compare(FieldInfo x, FieldInfo y)
            {
                return String.Compare(x.Name, y.Name, StringComparison.Ordinal);
            }
        }

        private static bool CheckForCustomSerialization(Type t)
        {
            bool hasCustomSerializer = false;
            bool hasCustomDeserializer = false;

            foreach (var method in t.GetMethods())
            {
                if (method.GetCustomAttributes(typeof (SerializerMethodAttribute), true).Length > 0)
                    hasCustomSerializer = true;
                else if (method.GetCustomAttributes(typeof (DeserializerMethodAttribute), true).Length > 0)
                    hasCustomDeserializer = true;
                }

            if (hasCustomDeserializer && hasCustomSerializer)
                return true;

            if (hasCustomDeserializer)
                throw new OrleansException(String.Format("Class {0} has a custom deserializer but no custom serializer", t));

            if (hasCustomSerializer)
                throw new OrleansException(String.Format("Class {0} has a custom serializer but no custom deserializer", t));

            return false;
        }

        private static bool CheckForCustomCopier(Type t)
        {
            return t.GetMethods().Any(method => method.GetCustomAttributes(typeof(CopierMethodAttribute), true).Length > 0);
        }

        private static void ImportFieldNamespaces(Type t, CodeNamespaceImportCollection imports)
        {
            if (!String.IsNullOrEmpty(t.Namespace))
            {
                imports.Add(new CodeNamespaceImport(t.Namespace));
            }

            if (!t.IsGenericType) return;

                foreach (var param in t.GetGenericArguments())
                    if (!param.IsGenericParameter)
                        ImportFieldNamespaces(param, imports);
                    }
                }
            }
