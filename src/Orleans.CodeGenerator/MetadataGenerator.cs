using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal static class MetadataGenerator
    {
        public static ClassDeclarationSyntax GenerateMetadata(Compilation compilation, MetadataModel metadataModel, LibraryTypes libraryTypes)
        {
            var configParam = "config".ToIdentifierName();
            var addSerializerMethod = configParam.Member("Serializers").Member("Add");
            var addCopierMethod = configParam.Member("Copiers").Member("Add");
            var addConverterMethod = configParam.Member("Converters").Member("Add");
            var body = new List<StatementSyntax>();
            body.AddRange(
                metadataModel.SerializableTypes.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addSerializerMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(GetCodecTypeName(type)))))))
                ));
            body.AddRange(
                metadataModel.SerializableTypes.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addCopierMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(GetCopierTypeName(type)))))))
                ));
            body.AddRange(
                metadataModel.DetectedCopiers.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addCopierMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(type.ToOpenTypeSyntax()))))))
                ));
            body.AddRange(
                metadataModel.DetectedSerializers.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addSerializerMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(type.ToOpenTypeSyntax()))))))
                ));
            body.AddRange(
                metadataModel.DetectedConverters.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addConverterMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(type.ToOpenTypeSyntax()))))))));
            var addProxyMethod = configParam.Member("InterfaceProxies").Member("Add");
            body.AddRange(
                metadataModel.GeneratedProxies.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addProxyMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(type.TypeSyntax))))))
                ));
            var addInvokableInterfaceMethod = configParam.Member("Interfaces").Member("Add");
            body.AddRange(
                metadataModel.InvokableInterfaces.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addInvokableInterfaceMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(type.InterfaceType.ToOpenTypeSyntax()))))))
                ));
            var addInvokableInterfaceImplementationMethod = configParam.Member("InterfaceImplementations").Member("Add");
            body.AddRange(
                metadataModel.InvokableInterfaceImplementations.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addInvokableInterfaceImplementationMethod ,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(type.ToOpenTypeSyntax()))))))
                ));

            var addActivatorMethod = configParam.Member("Activators").Member("Add");
            body.AddRange(
                metadataModel.ActivatableTypes.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addActivatorMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(GetActivatorTypeName(type)))))))
                ));
            body.AddRange(
                metadataModel.DetectedActivators.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addActivatorMethod,
                                ArgumentList(
                                    SingletonSeparatedList(
                                        Argument(TypeOfExpression(type.ToOpenTypeSyntax()))))))
                ));

            var addWellKnownTypeIdMethod = configParam.Member("WellKnownTypeIds").Member("Add");
            body.AddRange(
                metadataModel.WellKnownTypeIds.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addWellKnownTypeIdMethod,
                                ArgumentList(SeparatedList(
                                    new[]
                                    {
                                        Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(type.Id))),
                                        Argument(TypeOfExpression(type.Type))
                                    }))))
                ));
            
            var addTypeAliasMethod = configParam.Member("WellKnownTypeAliases").Member("Add");
            body.AddRange(
                metadataModel.TypeAliases.Select(
                    type =>
                        (StatementSyntax)ExpressionStatement(
                            InvocationExpression(
                                addTypeAliasMethod,
                                ArgumentList(SeparatedList(
                                    new[]
                                    {
                                        Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(type.Alias))),
                                        Argument(TypeOfExpression(type.Type))
                                    }))))
                ));

            var configType = libraryTypes.TypeManifestOptions;
            var configureMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Configure")
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(
                    Parameter(configParam.Identifier).WithType(configType.ToTypeSyntax()))
                .AddBodyStatements(body.ToArray());

            var interfaceType = libraryTypes.ITypeManifestProvider;
            return ClassDeclaration("Metadata_" + SyntaxGeneration.Identifier.SanitizeIdentifierName(compilation.AssemblyName))
                .AddBaseListTypes(SimpleBaseType(interfaceType.ToTypeSyntax()))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(configureMethod);
        }

        public static TypeSyntax GetCodecTypeName(this ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = SerializerGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name += $"<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetCopierTypeName(this ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = CopierGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name += $"<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetActivatorTypeName(this ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = ActivatorGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name += $"<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }
    }
}