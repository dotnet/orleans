namespace Orleans.CodeGenerator
{
    using System;
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
    using static Microsoft.CodeAnalysis.SyntaxNodeExtensions;

    /// <summary>
    /// Code generator which generates serializers.
    /// </summary>
    public static class SerializerGenerator
    {
        private static readonly TypeFormattingOptions GeneratedTypeNameOptions = new TypeFormattingOptions(
            ClassSuffix,
            includeGenericParameters: false,
            includeTypeParameters: false,
            nestedClassSeparator: '_',
            includeGlobal: false);

        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "Serializer";

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="type">The grain interface type.</param>
        /// <param name="onEncounteredType">
        /// The callback invoked when a type is encountered.
        /// </param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static TypeDeclarationSyntax GenerateClass(Type type, Action<Type> onEncounteredType)
        {
            var typeInfo = type.GetTypeInfo();
            var genericTypes = typeInfo.IsGenericTypeDefinition
                                   ? typeInfo.GetGenericArguments().Select(_ => SF.TypeParameter(_.ToString())).ToArray()
                                   : new TypeParameterSyntax[0];

            var attributes = new List<AttributeSyntax>
            {
                CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
#if !NETSTANDARD
                SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax()),
#endif
                SF.Attribute(typeof(SerializerAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(SF.TypeOfExpression(type.GetTypeSyntax(includeGenericParameters: false))))
            };

            var className = CodeGeneratorCommon.ClassPrefix + type.GetParseableName(GeneratedTypeNameOptions);
            var fields = GetFields(type);

            // Mark each field type for generation
            foreach (var field in fields)
            {
                var fieldType = field.FieldInfo.FieldType;
                onEncounteredType(fieldType);
            }

            var members = new List<MemberDeclarationSyntax>(GenerateStaticFields(fields))
            {
                GenerateDeepCopierMethod(type, fields),
                GenerateSerializerMethod(type, fields),
                GenerateDeserializerMethod(type, fields),
            };
            
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

            return classDeclaration;
        }

        /// <summary>
        /// Returns syntax for the deserializer method.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fields">The fields.</param>
        /// <returns>Syntax for the deserializer method.</returns>
        private static MemberDeclarationSyntax GenerateDeserializerMethod(Type type, List<FieldInfoMember> fields)
        {
            Expression<Action> deserializeInner =
                () => SerializationManager.DeserializeInner(default(Type), default(IDeserializationContext));
            var contextParameter = SF.IdentifierName("context");

            var resultDeclaration =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(type.GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("result")
                                .WithInitializer(SF.EqualsValueClause(GetObjectCreationExpressionSyntax(type)))));
            var resultVariable = SF.IdentifierName("result");

            var body = new List<StatementSyntax> { resultDeclaration };

            // Value types cannot be referenced, only copied, so there is no need to box & record instances of value types.
            if (!type.GetTypeInfo().IsValueType)
            {
                // Record the result for cyclic deserialization.
                Expression<Action<IDeserializationContext>> recordObject = ctx => ctx.RecordObject(default(object));
                var currentSerializationContext = contextParameter;
                body.Add(
                    SF.ExpressionStatement(
                        recordObject.Invoke(currentSerializationContext)
                            .AddArgumentListArguments(SF.Argument(resultVariable))));
            }

            // Deserialize all fields.
            foreach (var field in fields)
            {
                var deserialized =
                    deserializeInner.Invoke()
                        .AddArgumentListArguments(
                            SF.Argument(SF.TypeOfExpression(field.Type)),
                            SF.Argument(contextParameter));
                body.Add(
                    SF.ExpressionStatement(
                        field.GetSetter(
                            resultVariable,
                            SF.CastExpression(field.Type, deserialized))));
            }

            body.Add(SF.ReturnStatement(SF.CastExpression(type.GetTypeSyntax(), resultVariable)));
            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "Deserializer")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("expected")).WithType(typeof(Type).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("context")).WithType(typeof(IDeserializationContext).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        SF.AttributeList()
                            .AddAttributes(SF.Attribute(typeof(DeserializerMethodAttribute).GetNameSyntax())));
        }

        private static MemberDeclarationSyntax GenerateSerializerMethod(Type type, List<FieldInfoMember> fields)
        {
            Expression<Action> serializeInner =
                () =>
                SerializationManager.SerializeInner(default(object), default(ISerializationContext), default(Type));
            var contextParameter = SF.IdentifierName("context");

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
                                SF.Argument(contextParameter),
                                SF.Argument(SF.TypeOfExpression(field.FieldInfo.FieldType.GetTypeSyntax())))));
            }

            return
                SF.MethodDeclaration(typeof(void).GetTypeSyntax(), "Serializer")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("untypedInput")).WithType(typeof(object).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("context")).WithType(typeof(ISerializationContext).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("expected")).WithType(typeof(Type).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        SF.AttributeList()
                            .AddAttributes(SF.Attribute(typeof(SerializerMethodAttribute).GetNameSyntax())));
        }

        /// <summary>
        /// Returns syntax for the deep copier method.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fields">The fields.</param>
        /// <returns>Syntax for the deep copier method.</returns>
        private static MemberDeclarationSyntax GenerateDeepCopierMethod(Type type, List<FieldInfoMember> fields)
        {
            var originalVariable = SF.IdentifierName("original");
            var inputVariable = SF.IdentifierName("input");
            var resultVariable = SF.IdentifierName("result");

            var body = new List<StatementSyntax>();
            if (type.GetTypeInfo().GetCustomAttribute<ImmutableAttribute>() != null)
            {
                // Immutable types do not require copying.
                var typeName = type.GetParseableName(new TypeFormattingOptions(includeGlobal: false));
                var comment = SF.Comment($"// No deep copy required since {typeName} is marked with the [Immutable] attribute.");
                body.Add(SF.ReturnStatement(originalVariable).WithLeadingTrivia(comment));
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

                // Record this serialization.
                Expression<Action<ICopyContext>> recordObject =
                    ctx => ctx.RecordCopy(default(object), default(object));
                var context = SF.IdentifierName("context");
                body.Add(
                    SF.ExpressionStatement(
                        recordObject.Invoke(context)
                            .AddArgumentListArguments(SF.Argument(originalVariable), SF.Argument(resultVariable))));

                // Copy all members from the input to the result.
                foreach (var field in fields)
                {
                    body.Add(SF.ExpressionStatement(field.GetSetter(resultVariable, field.GetGetter(inputVariable, context))));
                }

                body.Add(SF.ReturnStatement(resultVariable));
            }

            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "DeepCopier")
                  .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                  .AddParameterListParameters(
                      SF.Parameter(SF.Identifier("original")).WithType(typeof(object).GetTypeSyntax()),
                      SF.Parameter(SF.Identifier("context")).WithType(typeof(ICopyContext).GetTypeSyntax()))
                  .AddBodyStatements(body.ToArray())
                  .AddAttributeLists(
                      SF.AttributeList().AddAttributes(SF.Attribute(typeof(CopierMethodAttribute).GetNameSyntax())));
        }

        /// <summary>
        /// Returns syntax for the static fields of the serializer class.
        /// </summary>
        /// <param name="fields">The fields.</param>
        /// <returns>Syntax for the static fields of the serializer class.</returns>
        private static MemberDeclarationSyntax[] GenerateStaticFields(List<FieldInfoMember> fields)
        {
            var result = new List<MemberDeclarationSyntax>();

            // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
            Expression<Action<TypeInfo>> getField = _ => _.GetField(string.Empty, BindingFlags.Default);
            Expression<Action<Type>> getTypeInfo = _ => _.GetTypeInfo();
            Expression<Action> getGetter = () => SerializationManager.GetGetter(default(FieldInfo));
            Expression<Action> getReferenceSetter = () => SerializationManager.GetReferenceSetter(default(FieldInfo));
            Expression<Action> getValueSetter = () => SerializationManager.GetValueSetter(default(FieldInfo));

            // Expressions for specifying binding flags.
            var bindingFlags = SyntaxFactoryExtensions.GetBindingFlagsParenthesizedExpressionSyntax(
                   SyntaxKind.BitwiseOrExpression,
                   BindingFlags.Instance,
                   BindingFlags.NonPublic,
                   BindingFlags.Public);

            // Add each field and initialize it.
            foreach (var field in fields)
            {
                var fieldInfo =
                    getField.Invoke(getTypeInfo.Invoke(SF.TypeOfExpression(field.FieldInfo.DeclaringType.GetTypeSyntax())))
                        .AddArgumentListArguments(
                            SF.Argument(field.FieldInfo.Name.GetLiteralExpression()),
                            SF.Argument(bindingFlags));
                var fieldInfoVariable =
                    SF.VariableDeclarator(field.InfoFieldName).WithInitializer(SF.EqualsValueClause(fieldInfo));
                var fieldInfoField = SF.IdentifierName(field.InfoFieldName);

                if (!field.IsGettableProperty || !field.IsSettableProperty)
                {
                    result.Add(
                        SF.FieldDeclaration(
                            SF.VariableDeclaration(typeof(FieldInfo).GetTypeSyntax()).AddVariables(fieldInfoVariable))
                            .AddModifiers(
                                SF.Token(SyntaxKind.PrivateKeyword),
                                SF.Token(SyntaxKind.StaticKeyword),
                                SF.Token(SyntaxKind.ReadOnlyKeyword)));
                }

                // Declare the getter for this field.
                if (!field.IsGettableProperty)
                {
                    var getterType =
                        typeof(Func<,>).MakeGenericType(field.FieldInfo.DeclaringType, field.FieldInfo.FieldType)
                            .GetTypeSyntax();
                    var fieldGetterVariable =
                        SF.VariableDeclarator(field.GetterFieldName)
                            .WithInitializer(
                                SF.EqualsValueClause(
                                    SF.CastExpression(
                                        getterType,
                                        getGetter.Invoke().AddArgumentListArguments(SF.Argument(fieldInfoField)))));
                    result.Add(
                        SF.FieldDeclaration(SF.VariableDeclaration(getterType).AddVariables(fieldGetterVariable))
                            .AddModifiers(
                                SF.Token(SyntaxKind.PrivateKeyword),
                                SF.Token(SyntaxKind.StaticKeyword),
                                SF.Token(SyntaxKind.ReadOnlyKeyword)));
                }

                if (!field.IsSettableProperty)
                {
                    if (field.FieldInfo.DeclaringType != null && field.FieldInfo.DeclaringType.GetTypeInfo().IsValueType)
                    {
                        var setterType =
                            typeof(SerializationManager.ValueTypeSetter<,>).MakeGenericType(
                                field.FieldInfo.DeclaringType,
                                field.FieldInfo.FieldType).GetTypeSyntax();

                        var fieldSetterVariable =
                            SF.VariableDeclarator(field.SetterFieldName)
                                .WithInitializer(
                                    SF.EqualsValueClause(
                                        SF.CastExpression(
                                            setterType,
                                            getValueSetter.Invoke()
                                                .AddArgumentListArguments(SF.Argument(fieldInfoField)))));
                        result.Add(
                            SF.FieldDeclaration(SF.VariableDeclaration(setterType).AddVariables(fieldSetterVariable))
                                .AddModifiers(
                                    SF.Token(SyntaxKind.PrivateKeyword),
                                    SF.Token(SyntaxKind.StaticKeyword),
                                    SF.Token(SyntaxKind.ReadOnlyKeyword)));
                    }
                    else
                    {
                        var setterType =
                            typeof(Action<,>).MakeGenericType(field.FieldInfo.DeclaringType, field.FieldInfo.FieldType)
                                .GetTypeSyntax();

                        var fieldSetterVariable =
                            SF.VariableDeclarator(field.SetterFieldName)
                                .WithInitializer(
                                    SF.EqualsValueClause(
                                        SF.CastExpression(
                                            setterType,
                                            getReferenceSetter.Invoke()
                                                .AddArgumentListArguments(SF.Argument(fieldInfoField)))));

                        result.Add(
                            SF.FieldDeclaration(SF.VariableDeclaration(setterType).AddVariables(fieldSetterVariable))
                                .AddModifiers(
                                    SF.Token(SyntaxKind.PrivateKeyword),
                                    SF.Token(SyntaxKind.StaticKeyword),
                                    SF.Token(SyntaxKind.ReadOnlyKeyword)));
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Returns syntax for initializing a new instance of the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>Syntax for initializing a new instance of the provided type.</returns>
        private static ExpressionSyntax GetObjectCreationExpressionSyntax(Type type)
        {
            ExpressionSyntax result;
            var typeInfo = type.GetTypeInfo();

            if (typeInfo.IsValueType)
            {
                // Use the default value.
                result = SF.DefaultExpression(typeInfo.AsType().GetTypeSyntax());
            }
            else if (typeInfo.GetConstructor(Type.EmptyTypes) != null)
            {
                // Use the default constructor.
                result = SF.ObjectCreationExpression(typeInfo.AsType().GetTypeSyntax()).AddArgumentListArguments();
            }
            else
            {
                // Create an unformatted object.
                Expression<Func<object>> getUninitializedObject =
#if NETSTANDARD
                    () => SerializationManager.GetUninitializedObjectWithFormatterServices(default(Type));
#else
                    () => FormatterServices.GetUninitializedObject(default(Type));
#endif
                result = SF.CastExpression(
                    type.GetTypeSyntax(),
                    getUninitializedObject.Invoke()
                        .AddArgumentListArguments(
                            SF.Argument(SF.TypeOfExpression(typeInfo.AsType().GetTypeSyntax()))));
            }

            return result;
        }

        /// <summary>
        /// Returns a sorted list of the fields of the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A sorted list of the fields of the provided type.</returns>
        private static List<FieldInfoMember> GetFields(Type type)
        {
            var result =
                type.GetAllFields()
                    .Where(field => field.GetCustomAttribute<NonSerializedAttribute>() == null)
                    .Select((info, i) => new FieldInfoMember { FieldInfo = info, FieldNumber = i })
                    .ToList();
            result.Sort(FieldInfoMember.Comparer.Instance);
            return result;
        }

        /// <summary>
        /// Represents a field.
        /// </summary>
        private class FieldInfoMember
        {
            private PropertyInfo property;

            /// <summary>
            /// Gets or sets the underlying <see cref="FieldInfo"/> instance.
            /// </summary>
            public FieldInfo FieldInfo { get; set; }

            /// <summary>
            /// Sets the ordinal assigned to this field.
            /// </summary>
            public int FieldNumber { private get; set; }

            /// <summary>
            /// Gets the name of the field info field.
            /// </summary>
            public string InfoFieldName
            {
                get
                {
                    return "field" + this.FieldNumber;
                }
            }

            /// <summary>
            /// Gets the name of the getter field.
            /// </summary>
            public string GetterFieldName
            {
                get
                {
                    return "getField" + this.FieldNumber;
                }
            }

            /// <summary>
            /// Gets the name of the setter field.
            /// </summary>
            public string SetterFieldName
            {
                get
                {
                    return "setField" + this.FieldNumber;
                }
            }

            /// <summary>
            /// Gets a value indicating whether or not this field represents a property with an accessible, non-obsolete getter. 
            /// </summary>
            public bool IsGettableProperty
            {
                get
                {
                    return this.PropertyInfo != null && this.PropertyInfo.GetGetMethod() != null && !this.IsObsolete;
                }
            }

            /// <summary>
            /// Gets a value indicating whether or not this field represents a property with an accessible, non-obsolete setter. 
            /// </summary>
            public bool IsSettableProperty
            {
                get
                {
                    return this.PropertyInfo != null && this.PropertyInfo.GetSetMethod() != null && !this.IsObsolete;
                }
            }

            /// <summary>
            /// Gets syntax representing the type of this field.
            /// </summary>
            public TypeSyntax Type
            {
                get
                {
                    return this.FieldInfo.FieldType.GetTypeSyntax();
                }
            }

            /// <summary>
            /// Gets the <see cref="PropertyInfo"/> which this field is the backing property for, or
            /// <see langword="null" /> if this is not the backing field of an auto-property.
            /// </summary>
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
                        this.property = this.FieldInfo.DeclaringType.GetTypeInfo()
                            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    }

                    return this.property;
                }
            }

            /// <summary>
            /// Gets a value indicating whether or not this field is obsolete.
            /// </summary>
            private bool IsObsolete
            {
                get
                {
                    var obsoleteAttr = this.FieldInfo.GetCustomAttribute<ObsoleteAttribute>();

                    // Get the attribute from the property, if present.
                    if (this.property != null && obsoleteAttr == null)
                    {
                        obsoleteAttr = this.property.GetCustomAttribute<ObsoleteAttribute>();
                    }
                    
                    return obsoleteAttr != null;
                }
            }

            /// <summary>
            /// Returns syntax for retrieving the value of this field, deep copying it if necessary.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <param name="serializationContextExpression">The expression used to retrieve the serialization context.</param>
            /// <param name="forceAvoidCopy">Whether or not to ensure that no copy of the field is made.</param>
            /// <returns>Syntax for retrieving the value of this field.</returns>
            public ExpressionSyntax GetGetter(ExpressionSyntax instance, ExpressionSyntax serializationContextExpression = null, bool forceAvoidCopy = false)
            {
                // Retrieve the value of the field.
                var getValueExpression = this.GetValueExpression(instance);

                // Avoid deep-copying the field if possible.
                if (forceAvoidCopy || this.FieldInfo.FieldType.IsOrleansShallowCopyable())
                {
                    // Return the value without deep-copying it.
                    return getValueExpression;
                }

                // Addressable arguments must be converted to references before passing.
                // IGrainObserver instances cannot be directly converted to references, therefore they are not included.
                ExpressionSyntax deepCopyValueExpression;
                if (typeof(IAddressable).IsAssignableFrom(this.FieldInfo.FieldType)
                    && this.FieldInfo.FieldType.GetTypeInfo().IsInterface
                    && !typeof(IGrainObserver).IsAssignableFrom(this.FieldInfo.FieldType))
                {
                    var getAsReference = getValueExpression.Member(
                        (IAddressable grain) => grain.AsReference<IGrain>(),
                        this.FieldInfo.FieldType);

                    // If the value is not a GrainReference, convert it to a strongly-typed GrainReference.
                    // C#: (value == null || value is GrainReference) ? value : value.AsReference<TInterface>()
                    deepCopyValueExpression =
                        SF.ConditionalExpression(
                            SF.ParenthesizedExpression(
                                SF.BinaryExpression(
                                    SyntaxKind.LogicalOrExpression,
                                    SF.BinaryExpression(
                                        SyntaxKind.EqualsExpression,
                                        getValueExpression,
                                        SF.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                                    SF.BinaryExpression(
                                        SyntaxKind.IsExpression,
                                        getValueExpression,
                                        typeof(GrainReference).GetTypeSyntax()))),
                            getValueExpression,
                            SF.InvocationExpression(getAsReference));
                }
                else
                {
                    deepCopyValueExpression = getValueExpression;
                }

                // Deep-copy the value.
                Expression<Action> deepCopyInner = () => SerializationManager.DeepCopyInner(default(object), default(ICopyContext));
                var typeSyntax = this.FieldInfo.FieldType.GetTypeSyntax();
                return SF.CastExpression(
                    typeSyntax,
                    deepCopyInner.Invoke()
                                 .AddArgumentListArguments(
                                     SF.Argument(deepCopyValueExpression),
                                     SF.Argument(serializationContextExpression)));
            }

            /// <summary>
            /// Returns syntax for setting the value of this field.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <param name="value">Syntax for the new value.</param>
            /// <returns>Syntax for setting the value of this field.</returns>
            public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value)
            {
                // If the field is the backing field for an accessible auto-property use the property directly.
                if (this.PropertyInfo != null && this.PropertyInfo.GetSetMethod() != null && !this.IsObsolete)
                {
                    return SF.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        instance.Member(this.PropertyInfo.Name),
                        value);
                }

                var instanceArg = SF.Argument(instance);
                if (this.FieldInfo.DeclaringType != null && this.FieldInfo.DeclaringType.GetTypeInfo().IsValueType)
                {
                    instanceArg = instanceArg.WithRefOrOutKeyword(SF.Token(SyntaxKind.RefKeyword));
                }

                return
                    SF.InvocationExpression(SF.IdentifierName(this.SetterFieldName))
                        .AddArgumentListArguments(instanceArg, SF.Argument(value));
            }

            /// <summary>
            /// Returns syntax for retrieving the value of this field.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <returns>Syntax for retrieving the value of this field.</returns>
            private ExpressionSyntax GetValueExpression(ExpressionSyntax instance)
            {
                // If the field is the backing field for an accessible auto-property use the property directly.
                ExpressionSyntax result;
                if (this.PropertyInfo != null && this.PropertyInfo.GetGetMethod() != null && !this.IsObsolete)
                {
                    result = instance.Member(this.PropertyInfo.Name);
                }
                else
                {
                    // Retrieve the field using the generated getter.
                    result =
                        SF.InvocationExpression(SF.IdentifierName(this.GetterFieldName))
                            .AddArgumentListArguments(SF.Argument(instance));
                }

                return result;
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

                public int Compare(FieldInfoMember x, FieldInfoMember y)
                {
                    return string.Compare(x.FieldInfo.Name, y.FieldInfo.Name, StringComparison.Ordinal);
                }
            }
        }
    }
}
