using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System;

namespace Orleans.CodeGenerator
{
    internal class MetadataGenerator
    {
        private readonly CodeGenerator _codeGenerator;

        public MetadataGenerator(CodeGenerator codeGenerator)
        {
            _codeGenerator = codeGenerator;
        }

        private MetadataModel MetadataModel => _codeGenerator.MetadataModel;

        public ClassDeclarationSyntax GenerateMetadata()
        {
            var configParam = "config".ToIdentifierName();
            var addSerializerMethod = configParam.Member("Serializers").Member("Add");
            var addCopierMethod = configParam.Member("Copiers").Member("Add");
            var addConverterMethod = configParam.Member("Converters").Member("Add");
            var body = new List<StatementSyntax>();

            foreach (var type in MetadataModel.SerializableTypes)
            {
                body.Add(ExpressionStatement(InvocationExpression(addSerializerMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(GetCodecTypeName(type))))))));
            }

            foreach (var type in MetadataModel.SerializableTypes)
            {
                if (type.IsEnumType) continue;

                if (!MetadataModel.DefaultCopiers.TryGetValue(type, out var typeName))
                    typeName = GetCopierTypeName(type);

                body.Add(ExpressionStatement(InvocationExpression(addCopierMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(typeName)))))));
            }

            foreach (var type in MetadataModel.DetectedCopiers)
            {
                body.Add(ExpressionStatement(InvocationExpression(addCopierMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            foreach (var type in MetadataModel.DetectedSerializers)
            {
                body.Add(ExpressionStatement(InvocationExpression(addSerializerMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            foreach (var type in MetadataModel.DetectedConverters)
            {
                body.Add(ExpressionStatement(InvocationExpression(addConverterMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addProxyMethod = configParam.Member("InterfaceProxies").Member("Add");
            foreach (var type in MetadataModel.GeneratedProxies)
            {
                body.Add(ExpressionStatement(InvocationExpression(addProxyMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.TypeSyntax)))))));
            }

            var addInvokableInterfaceMethod = configParam.Member("Interfaces").Member("Add");
            foreach (var type in MetadataModel.InvokableInterfaces.Values)
            {
                body.Add(ExpressionStatement(InvocationExpression(addInvokableInterfaceMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.InterfaceType.ToOpenTypeSyntax())))))));
            }

            var addInvokableInterfaceImplementationMethod = configParam.Member("InterfaceImplementations").Member("Add");
            foreach (var type in MetadataModel.InvokableInterfaceImplementations)
            {
                body.Add(ExpressionStatement(InvocationExpression(addInvokableInterfaceImplementationMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addActivatorMethod = configParam.Member("Activators").Member("Add");
            foreach (var type in MetadataModel.ActivatableTypes)
            {
                body.Add(ExpressionStatement(InvocationExpression(addActivatorMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(GetActivatorTypeName(type))))))));
            }

            foreach (var type in MetadataModel.DetectedActivators)
            {
                body.Add(ExpressionStatement(InvocationExpression(addActivatorMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addWellKnownTypeIdMethod = configParam.Member("WellKnownTypeIds").Member("Add");
            foreach (var type in MetadataModel.WellKnownTypeIds)
            {
                body.Add(ExpressionStatement(InvocationExpression(addWellKnownTypeIdMethod,
                    ArgumentList(SeparatedList(new[] { Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(type.Id))), Argument(TypeOfExpression(type.Type)) })))));
            }

            var addTypeAliasMethod = configParam.Member("WellKnownTypeAliases").Member("Add");
            foreach (var type in MetadataModel.TypeAliases)
            {
                body.Add(ExpressionStatement(InvocationExpression(addTypeAliasMethod,
                    ArgumentList(SeparatedList(new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(type.Alias))), Argument(TypeOfExpression(type.Type)) })))));
            }

            AddCompoundTypeAliases(configParam, body);

            var configType = _codeGenerator.LibraryTypes.TypeManifestOptions;
            var configureMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "ConfigureInner")
                .AddModifiers(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword))
                .AddParameterListParameters(
                    Parameter(configParam.Identifier).WithType(configType.ToTypeSyntax()))
                .AddBodyStatements(body.ToArray());

            var interfaceType = _codeGenerator.LibraryTypes.TypeManifestProviderBase;
            return ClassDeclaration("Metadata_" + SyntaxGeneration.Identifier.SanitizeIdentifierName(_codeGenerator.Compilation.AssemblyName))
                .AddBaseListTypes(SimpleBaseType(interfaceType.ToTypeSyntax()))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(configureMethod);
        }

        private void AddCompoundTypeAliases(IdentifierNameSyntax configParam, List<StatementSyntax> body)
        {
            // The goal is to emit a tree describing all of the generated invokers in the form:
            // ("inv", typeof(ProxyBaseType), typeof(ContainingInterface), "<MethodId>")
            // The first step is to collate the invokers into tree to ease the process of generating a tree in code.
            var nodeId = 0;
            AddCompoundTypeAliases(body, configParam.Member("CompoundTypeAliases"), MetadataModel.CompoundTypeAliases);
            void AddCompoundTypeAliases(List<StatementSyntax> body, ExpressionSyntax tree, CompoundTypeAliasTree aliases)
            {
                ExpressionSyntax node;

                if (aliases.Key.IsDefault)
                {
                    // At the root node, do not create a new node, just enumerate over the child nodes.
                    node = tree;
                }
                else
                {
                    var nodeName = IdentifierName($"n{++nodeId}");
                    node = nodeName;
                    var valueExpression = aliases.Value switch
                    {
                        { } type => Argument(TypeOfExpression(type)),
                        _ => null
                    };

                    // Get the arguments for the Add call
                    var addArguments = aliases.Key switch
                    {
                        { IsType: true } typeKey => valueExpression switch
                        {
                            // Call the two-argument Add overload to add a key and value.
                            { } argument => new[] { Argument(TypeOfExpression(typeKey.TypeValue.ToOpenTypeSyntax())), argument },

                            // Call the one-argument Add overload to add only a key.
                            _ => new[] { Argument(TypeOfExpression(typeKey.TypeValue.ToOpenTypeSyntax())) },
                        },
                        { IsString: true } stringKey => valueExpression switch
                        {
                            // Call the two-argument Add overload to add a key and value.
                            { } argument => new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(stringKey.StringValue))), argument },

                            // Call the one-argument Add overload to add only a key.
                            _ => new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(stringKey.StringValue))) },
                        },
                        _ => throw new InvalidOperationException("Unexpected alias key")
                    };

                    if (aliases.Children is { Count: > 0 })
                    {
                        // C#: var {newTree.Identifier} = {tree}.Add({addArguments});
                        body.Add(LocalDeclarationStatement(VariableDeclaration(
                            ParseTypeName("var"),
                            SingletonSeparatedList(VariableDeclarator(nodeName.Identifier).WithInitializer(EqualsValueClause(InvocationExpression(
                                tree.Member("Add"),
                                ArgumentList(SeparatedList(addArguments)))))))));
                    }
                    else
                    {
                        // Do not emit a variable.
                        // C#: {tree}.Add({addArguments});
                        body.Add(ExpressionStatement(InvocationExpression(tree.Member("Add"), ArgumentList(SeparatedList(addArguments)))));
                    }
                }

                if (aliases.Children is { Count: > 0 })
                {
                    foreach (var child in aliases.Children.Values)
                    {
                        AddCompoundTypeAliases(body, node, child);
                    }
                }
            }
        }

        public static TypeSyntax GetCodecTypeName(ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = SerializerGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetCopierTypeName(ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = CopierGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetActivatorTypeName(ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = ActivatorGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

    }
}