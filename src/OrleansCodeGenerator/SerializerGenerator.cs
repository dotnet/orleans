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
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Text.RegularExpressions;

    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Concurrency;
    using Orleans.Runtime;
    using Orleans.Serialization;

    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Code generator which generates serializers.
    /// </summary>
    public static class SerializerGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "Serializer";
        
        /// <summary>
        /// The suffix appended to the name of the generic serializer registration class.
        /// </summary>
        private const string RegistererClassSuffix = "Registerer";
        
        /// <summary>
        /// Returns true if the provided type is a seed type for serialization generation.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>true if the provided type is a seed type for serialization generation.</returns>
        internal static bool IsSerializationSeedType(Type type)
        {
            // Skip compiler-generated types (whose names begin with a '<' character).
            return !type.Name.StartsWith("<") && type.GetCustomAttribute<GeneratedCodeAttribute>() == null
                   && type.GetCustomAttribute<NonSerializableAttribute>() == null
                   && !TypeUtils.IsInNamespace(type, SerializerGenerationManager.IgnoredNamespaces);
        }

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="type">
        ///     The grain interface type.
        /// </param>
        /// <param name="onEncounteredType">
        /// The callback invoked when a type is encountered.
        /// </param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static IEnumerable<TypeDeclarationSyntax> GenerateClass(Type type, Action<Type> onEncounteredType)
        {
            var genericTypes = type.IsGenericTypeDefinition
                                   ? type.GetGenericArguments().Select(_ => SF.TypeParameter(_.ToString())).ToArray()
                                   : new TypeParameterSyntax[0];

            var attributes = new List<AttributeSyntax>
            {
                CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax()),
                SF.Attribute(typeof(SerializerAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(SF.TypeOfExpression(type.GetTypeSyntax(includeGenericParameters: false))))
            };

            var className = CodeGeneratorCommon.ClassPrefix
                            + TypeUtils.GetSimpleTypeName(type, _ => !_.IsGenericParameter).Replace('.', '_')
                            + ClassSuffix;
            var fields = GetFields(type);

            // Mark each field type for generation
            foreach (var field in fields)
            {
                var fieldType = field.FieldInfo.FieldType;
                onEncounteredType(fieldType);
            }

            var members = new List<MemberDeclarationSyntax>(GetFieldInfoFields(fields))
            {
                GenerateDeepCopierMethod(type, fields),
                GenerateSerializerMethod(type, fields),
                GenerateDeserializerMethod(type, fields),
            };

            if (type.IsConstructedGenericType || !type.IsGenericTypeDefinition)
            {
                members.Add(GenerateRegisterMethod(type));
                members.Add(GenerateConstructor(className));
                attributes.Add(SF.Attribute(typeof(RegisterSerializerAttribute).GetNameSyntax()));
            }

            var classDeclaration =
                SF.ClassDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddAttributeLists(SF.AttributeList().AddAttributes(attributes.ToArray()))
                    .AddMembers(members.ToArray())
                    .AddConstraintClauses(type.GetTypeConstraintSyntax());
            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            var classes = new List<TypeDeclarationSyntax> { classDeclaration };

            if (type.IsGenericTypeDefinition)
            {
                // Create a generic representation of the serializer type.
                var serializerType =
                    SF.GenericName(classDeclaration.Identifier)
                        .WithTypeArgumentList(
                            SF.TypeArgumentList()
                                .AddArguments(
                                    type.GetGenericArguments()
                                        .Select(_ => SF.OmittedTypeArgument())
                                        .Cast<TypeSyntax>()
                                        .ToArray()));
                classes.Add(
                    SF.ClassDeclaration(className + RegistererClassSuffix)
                        .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                        .AddAttributeLists(
                            SF.AttributeList()
                                .AddAttributes(
                                    CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                                    SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax()),
                                    SF.Attribute(typeof(RegisterSerializerAttribute).GetNameSyntax())))
                        .AddMembers(
                            GenerateMasterRegisterMethod(type, serializerType),
                            GenerateConstructor(className + RegistererClassSuffix)));
            }

            return classes;
        }

        private static MemberDeclarationSyntax GenerateDeserializerMethod(Type type, List<FieldInfoMember> fields)
        {
            Expression<Action> deserializeInner =
                () => SerializationManager.DeserializeInner(default(Type), default(BinaryTokenStreamReader));
            var streamParameter = SF.IdentifierName("stream");

            var resultDeclaration =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(type.GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("result")
                                .WithInitializer(SF.EqualsValueClause(GetObjectCreationExpressionSyntax(type)))));
            var resultVariable = SF.IdentifierName("result");
            var boxedResultVariable = resultVariable;

            var body = new List<StatementSyntax> { resultDeclaration };

            if (type.IsValueType)
            {
                // For value types, we need to box the result for reflection-based setters to work.
                body.Add(SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(typeof(object).GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("boxedResult").WithInitializer(SF.EqualsValueClause(resultVariable)))));
                boxedResultVariable = SF.IdentifierName("boxedResult");
            }
            
            // Record the result for cyclic deserialization.
            Expression<Action> recordObject =
                () => DeserializationContext.Current.RecordObject(default(object));
            var currentSerializationContext =
                SyntaxFactory.AliasQualifiedName(
                    SF.IdentifierName(SF.Token(SyntaxKind.GlobalKeyword)),
                    SF.IdentifierName("Orleans"))
                    .Qualify("Serialization")
                    .Qualify("DeserializationContext")
                    .Qualify("Current");
            body.Add(
                SF.ExpressionStatement(
                    recordObject.Invoke(currentSerializationContext)
                        .AddArgumentListArguments(
                            SF.Argument(boxedResultVariable))));

            // Deserialize all fields.
            foreach (var field in fields)
            {
                var deserialized =
                    deserializeInner.Invoke()
                        .AddArgumentListArguments(
                            SF.Argument(SF.TypeOfExpression(field.Type)),
                            SF.Argument(streamParameter));
                body.Add(
                    SF.ExpressionStatement(
                        field.GetSetter(
                            resultVariable,
                            SF.CastExpression(field.Type, deserialized),
                            boxedResultVariable)));
            }

            body.Add(SF.ReturnStatement(SF.CastExpression(type.GetTypeSyntax(), boxedResultVariable)));
            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "Deserializer")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("expected")).WithType(typeof(Type).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("stream")).WithType(typeof(BinaryTokenStreamReader).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        SF.AttributeList()
                            .AddAttributes(SF.Attribute(typeof(DeserializerMethodAttribute).GetNameSyntax())));
        }

        private static MemberDeclarationSyntax GenerateSerializerMethod(Type type, List<FieldInfoMember> fields)
        {
            Expression<Action> serializeInner =
                () =>
                SerializationManager.SerializeInner(default(object), default(BinaryTokenStreamWriter), default(Type));

            var body = new List<StatementSyntax>
            {
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(type.GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("input")
                                .WithInitializer(
                                    SF.EqualsValueClause(
                                        SF.CastExpression(type.GetTypeSyntax(), SF.IdentifierName("untypedInput"))))))
            };

            var inputExpression = SF.IdentifierName("input");

            // Serialize all members.
            foreach (var field in fields)
            {
                body.Add(
                    SF.ExpressionStatement(
                        serializeInner.Invoke()
                            .AddArgumentListArguments(
                                SF.Argument(field.GetGetter(inputExpression, forceAvoidCopy: true)),
                                SF.Argument(SF.IdentifierName("stream")),
                                SF.Argument(SF.TypeOfExpression(field.FieldInfo.FieldType.GetTypeSyntax())))));
            }

            return
                SF.MethodDeclaration(typeof(void).GetTypeSyntax(), "Serializer")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("untypedInput")).WithType(typeof(object).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("stream")).WithType(typeof(BinaryTokenStreamWriter).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("expected")).WithType(typeof(Type).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        SF.AttributeList()
                            .AddAttributes(SF.Attribute(typeof(SerializerMethodAttribute).GetNameSyntax())));
        }

        private static MemberDeclarationSyntax GenerateDeepCopierMethod(Type type, List<FieldInfoMember> fields)
        {
            var originalVariable = SF.IdentifierName("original");
            var inputVariable = SF.IdentifierName("input");
            var resultVariable = SF.IdentifierName("result");
            var boxedResultVariable = resultVariable;

            var body = new List<StatementSyntax>();
            if (type.GetCustomAttribute<ImmutableAttribute>() != null)
            {
                // Immutable types do not require copying.
                body.Add(SF.ReturnStatement(originalVariable));
            }
            else
            {
                body.Add(
                    SF.LocalDeclarationStatement(
                        SF.VariableDeclaration(type.GetTypeSyntax())
                            .AddVariables(
                                SF.VariableDeclarator("input")
                                    .WithInitializer(
                                        SF.EqualsValueClause(
                                            SF.ParenthesizedExpression(
                                                SF.CastExpression(type.GetTypeSyntax(), originalVariable)))))));
                body.Add(
                    SF.LocalDeclarationStatement(
                        SF.VariableDeclaration(type.GetTypeSyntax())
                            .AddVariables(
                                SF.VariableDeclarator("result")
                                    .WithInitializer(SF.EqualsValueClause(GetObjectCreationExpressionSyntax(type))))));

                if (type.IsValueType)
                {
                    // For value types, we need to box the result for reflection-based setters to work.
                    body.Add(SF.LocalDeclarationStatement(
                        SF.VariableDeclaration(typeof(object).GetTypeSyntax())
                            .AddVariables(
                                SF.VariableDeclarator("boxedResult").WithInitializer(SF.EqualsValueClause(resultVariable)))));
                    boxedResultVariable = SF.IdentifierName("boxedResult");
                }

                // Record this serialization.
                Expression<Action> recordObject =
                    () => SerializationContext.Current.RecordObject(default(object), default(object));
                var currentSerializationContext =
                    SyntaxFactory.AliasQualifiedName(
                        SF.IdentifierName(SF.Token(SyntaxKind.GlobalKeyword)),
                        SF.IdentifierName("Orleans"))
                        .Qualify("Serialization")
                        .Qualify("SerializationContext")
                        .Qualify("Current");
                body.Add(
                    SF.ExpressionStatement(
                        recordObject.Invoke(currentSerializationContext)
                            .AddArgumentListArguments(SF.Argument(originalVariable), SF.Argument(boxedResultVariable))));

                // Copy all members from the input to the result.
                foreach (var field in fields)
                {
                    body.Add(SF.ExpressionStatement(field.GetSetter(boxedResultVariable, field.GetGetter(inputVariable))));
                }

                body.Add(SF.ReturnStatement(boxedResultVariable));
            }

            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "DeepCopier")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("original")).WithType(typeof(object).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        SF.AttributeList().AddAttributes(SF.Attribute(typeof(CopierMethodAttribute).GetNameSyntax())));
        }

        private static MemberDeclarationSyntax[] GetFieldInfoFields(List<FieldInfoMember> fields)
        {
            var result = new List<MemberDeclarationSyntax>();

            // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
            Expression<Action<Type>> getField = _ => _.GetField(string.Empty, BindingFlags.Default);

            // Expressions for specifying binding flags.
            var flags = SF.IdentifierName("System").Member("Reflection").Member("BindingFlags");
            var publicFlag = flags.Member(BindingFlags.Public.ToString());
            var nonPublicFlag = flags.Member(BindingFlags.NonPublic.ToString());
            var instanceFlag = flags.Member(BindingFlags.Instance.ToString());
            var bindingFlags =
                SF.ParenthesizedExpression(
                    SF.BinaryExpression(
                        SyntaxKind.BitwiseOrExpression,
                        publicFlag,
                        SF.BinaryExpression(SyntaxKind.BitwiseOrExpression, nonPublicFlag, instanceFlag)));

            // Add each field and initialize it.
            foreach (var field in fields)
            {
                var fieldInfo =
                    getField.Invoke(SF.TypeOfExpression(field.FieldInfo.DeclaringType.GetTypeSyntax()))
                        .AddArgumentListArguments(
                            SF.Argument(field.FieldInfo.Name.GetLiteralExpression()),
                            SF.Argument(bindingFlags));
                var fieldInfoVariable =
                    SF.VariableDeclarator(field.InfoFieldName).WithInitializer(SF.EqualsValueClause(fieldInfo));
                result.Add(
                    SF.FieldDeclaration(
                        SF.VariableDeclaration(typeof(FieldInfo).GetTypeSyntax()).AddVariables(fieldInfoVariable))
                        .AddModifiers(
                            SF.Token(SyntaxKind.PrivateKeyword),
                            SF.Token(SyntaxKind.StaticKeyword),
                            SF.Token(SyntaxKind.ReadOnlyKeyword)));
            }

            return result.ToArray();
        }

        private static ExpressionSyntax GetObjectCreationExpressionSyntax(Type type)
        {
            ExpressionSyntax result;
            if (type.IsValueType)
            {
                // Use the default value.
                result = SF.DefaultExpression(type.GetTypeSyntax());
            }
            else if (type.GetConstructor(Type.EmptyTypes) != null)
            {
                // Use the default constructor.
                result = SF.ObjectCreationExpression(type.GetTypeSyntax()).AddArgumentListArguments();
            }
            else
            {
                // Create an unformatted object.
                Expression<Func<object>> getUninitializedObject =
                    () => FormatterServices.GetUninitializedObject(default(Type));
                result = SF.CastExpression(
                    type.GetTypeSyntax(),
                    getUninitializedObject.Invoke()
                        .AddArgumentListArguments(
                            SF.Argument(SF.TypeOfExpression(type.GetTypeSyntax()))));
            }

            return result;
        }


        private static MemberDeclarationSyntax GenerateRegisterMethod(Type type)
        {
            Expression<Action> register =
                () =>
                SerializationManager.Register(
                    default(Type),
                    default(SerializationManager.DeepCopier),
                    default(SerializationManager.Serializer),
                    default(SerializationManager.Deserializer));
            return
                SF.MethodDeclaration(typeof(void).GetTypeSyntax(), "Register")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters()
                    .AddBodyStatements(
                        SF.ExpressionStatement(
                            register.Invoke()
                                .AddArgumentListArguments(
                                    SF.Argument(SF.TypeOfExpression(type.GetTypeSyntax())),
                                    SF.Argument(SF.IdentifierName("DeepCopier")),
                                    SF.Argument(SF.IdentifierName("Serializer")),
                                    SF.Argument(SF.IdentifierName("Deserializer")))));
        }

        private static ConstructorDeclarationSyntax GenerateConstructor(string className)
        {
            return
                SF.ConstructorDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters()
                    .AddBodyStatements(
                        SF.ExpressionStatement(
                            SF.InvocationExpression(SF.IdentifierName("Register")).AddArgumentListArguments()));
        }

        private static MemberDeclarationSyntax GenerateMasterRegisterMethod(Type type, TypeSyntax serializerType)
        {
            Expression<Action> register = () => SerializationManager.Register(default(Type), default(Type));
            return
                SF.MethodDeclaration(typeof(void).GetTypeSyntax(), "Register")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters()
                    .AddBodyStatements(
                        SF.ExpressionStatement(
                            register.Invoke()
                                .AddArgumentListArguments(
                                    SF.Argument(
                                        SF.TypeOfExpression(type.GetTypeSyntax(includeGenericParameters: false))),
                                    SF.Argument(SF.TypeOfExpression(serializerType)))));
        }

        private static List<FieldInfoMember> GetFields(Type type)
        {
            var result =
                type.GetAllFields()
                    .Where(field => field.GetCustomAttribute<NonSerializedAttribute>() == null)
                    .Select(
                        (info, i) => new FieldInfoMember { FieldInfo = info, InfoFieldName = string.Format("field{0}", i) })
                    .ToList();
            result.Sort(FieldInfoMember.Comparer.Instance);
            return result;
        }

        private class FieldInfoMember
        {
            private PropertyInfo property;
            public FieldInfo FieldInfo { get; set; }

            public string InfoFieldName { get; set; }

            public TypeSyntax Type
            {
                get
                {
                    return this.FieldInfo.FieldType.GetTypeSyntax();
                }
            }

            private PropertyInfo PropertyInfo
            {
                get
                {
                    if (this.property != null)
                    {
                        return this.property;
                    }

                    var propertyName = Regex.Match(this.FieldInfo.Name, "^<([^>]+)>.*$");
                    if (propertyName.Success && this.FieldInfo.DeclaringType != null)
                    {
                        var name = propertyName.Groups[1].Value;
                        this.property = this.FieldInfo.DeclaringType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    }

                    return this.property;
                }
            }

            private ExpressionSyntax FieldInfoExpression
            {
                get
                {
                    return SF.IdentifierName(this.InfoFieldName);
                }
            }

            public Expression GetGetExpression(Expression instance, bool forceAvoidCopy = false)
            {
                // If the field is the backing field for an auto-property, try to use the property directly.
                if (this.PropertyInfo != null && this.PropertyInfo.GetGetMethod() != null)
                {
                    return Expression.Property(instance, this.PropertyInfo);
                }

                if (forceAvoidCopy || this.FieldInfo.FieldType.IsOrleansShallowCopyable())
                {
                    // Shallow-copy the field.
                    return Expression.Field(instance, this.FieldInfo);
                }

                // Deep-copy the field.
                Expression<Func<object,object>> deepCopyInner = input => SerializationManager.DeepCopyInner(input);
                return Expression.Invoke(deepCopyInner, instance);
            }

            public Expression GetSetExpression(Expression instance, Expression value, Expression boxedInstance = null)
            {
                // If the field is the backing field for an auto-property, try to use the property directly.
                if (this.PropertyInfo != null && this.PropertyInfo.GetSetMethod() != null)
                {
                    return Expression.Assign(Expression.Property(instance ?? boxedInstance, this.PropertyInfo), value);
                }

                return Expression.Assign(Expression.Field(instance ?? boxedInstance, this.FieldInfo), value);
            }

            public ExpressionSyntax GetGetter(ExpressionSyntax instance, bool forceAvoidCopy = false)
            {
                Expression<Action> fieldGetter = () => this.FieldInfo.GetValue(default(object));
                var getFieldExpression =
                    fieldGetter.Invoke(this.FieldInfoExpression).AddArgumentListArguments(SF.Argument(instance));

                // If the field is the backing field for an auto-property, try to use the property directly.
                var propertyName = Regex.Match(this.FieldInfo.Name, "^<([^>]+)>.*$");
                if (propertyName.Success && this.FieldInfo.DeclaringType != null)
                {
                    var name = propertyName.Groups[1].Value;
                    var property = this.FieldInfo.DeclaringType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (property != null && property.GetGetMethod() != null)
                    {
                        return instance.Member(property.Name);
                    }
                }

                if (forceAvoidCopy || this.FieldInfo.FieldType.IsOrleansShallowCopyable())
                {
                    // Shallow-copy the field.
                    return getFieldExpression;
                }

                // Deep-copy the field.
                Expression<Action> deepCopyInner = () => SerializationManager.DeepCopyInner(default(object));
                return SF.CastExpression(
                    this.FieldInfo.FieldType.GetTypeSyntax(),
                    deepCopyInner.Invoke().AddArgumentListArguments(SF.Argument(getFieldExpression)));
            }

            public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value, ExpressionSyntax boxedInstance = null)
            {
                Expression<Action> fieldSetter = () => this.FieldInfo.SetValue(default(object), default(object));

                // If the field is the backing field for an auto-property, try to use the property directly.
                var propertyName = Regex.Match(this.FieldInfo.Name, "^<([^>]+)>.*$");
                if (propertyName.Success && this.FieldInfo.DeclaringType != null)
                {
                    var name = propertyName.Groups[1].Value;
                    var property = this.FieldInfo.DeclaringType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (property != null && property.GetSetMethod() != null)
                    {
                        return SF.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            instance.Member(property.Name),
                            value);
                    }
                }

                return fieldSetter.Invoke(this.FieldInfoExpression)
                    .AddArgumentListArguments(SF.Argument(boxedInstance ?? instance), SF.Argument(value));
            }

            /// <summary>
            /// A comparer for <see cref="FieldInfoMember"/> which compares by name.
            /// </summary>
            public class Comparer : IComparer<FieldInfoMember>
            {
                /// <summary>
                /// The singleton instance.
                /// </summary>
                private static readonly Comparer Singleton = new Comparer();

                public int Compare(FieldInfoMember x, FieldInfoMember y)
                {
                    return string.Compare(x.FieldInfo.Name, y.FieldInfo.Name, StringComparison.Ordinal);
                }

                /// <summary>
                /// Gets the singleton instance of this class.
                /// </summary>
                public static Comparer Instance
                {
                    get
                    {
                        return Singleton;
                    }
                }
            }
        }
    }
}