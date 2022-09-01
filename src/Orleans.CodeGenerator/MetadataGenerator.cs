using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
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

            foreach (var type in metadataModel.SerializableTypes)
            {
                body.Add(ExpressionStatement(InvocationExpression(addSerializerMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(GetCodecTypeName(type))))))));
            }

            foreach (var type in metadataModel.SerializableTypes)
            {
                if (type.IsEnumType) continue;

                if (!metadataModel.DefaultCopiers.TryGetValue(type, out var typeName))
                    typeName = GetCopierTypeName(type);

                body.Add(ExpressionStatement(InvocationExpression(addCopierMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(typeName)))))));
            }

            foreach (var type in metadataModel.DetectedCopiers)
            {
                body.Add(ExpressionStatement(InvocationExpression(addCopierMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            foreach (var type in metadataModel.DetectedSerializers)
            {
                body.Add(ExpressionStatement(InvocationExpression(addSerializerMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            foreach (var type in metadataModel.DetectedConverters)
            {
                body.Add(ExpressionStatement(InvocationExpression(addConverterMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addProxyMethod = configParam.Member("InterfaceProxies").Member("Add");
            foreach (var type in metadataModel.GeneratedProxies)
            {
                body.Add(ExpressionStatement(InvocationExpression(addProxyMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.TypeSyntax)))))));
            }

            var addInvokableInterfaceMethod = configParam.Member("Interfaces").Member("Add");
            foreach (var type in metadataModel.InvokableInterfaces)
            {
                body.Add(ExpressionStatement(InvocationExpression(addInvokableInterfaceMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.InterfaceType.ToOpenTypeSyntax())))))));
            }

            var addInvokableInterfaceImplementationMethod = configParam.Member("InterfaceImplementations").Member("Add");
            foreach (var type in metadataModel.InvokableInterfaceImplementations)
            {
                body.Add(ExpressionStatement(InvocationExpression(addInvokableInterfaceImplementationMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addActivatorMethod = configParam.Member("Activators").Member("Add");
            foreach (var type in metadataModel.ActivatableTypes)
            {
                body.Add(ExpressionStatement(InvocationExpression(addActivatorMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(GetActivatorTypeName(type))))))));
            }

            foreach (var type in metadataModel.DetectedActivators)
            {
                body.Add(ExpressionStatement(InvocationExpression(addActivatorMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addWellKnownTypeIdMethod = configParam.Member("WellKnownTypeIds").Member("Add");
            foreach (var type in metadataModel.WellKnownTypeIds)
            {
                body.Add(ExpressionStatement(InvocationExpression(addWellKnownTypeIdMethod,
                    ArgumentList(SeparatedList(new[] { Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(type.Id))), Argument(TypeOfExpression(type.Type)) })))));
            }

            var addTypeAliasMethod = configParam.Member("WellKnownTypeAliases").Member("Add");
            foreach (var type in metadataModel.TypeAliases)
            {
                body.Add(ExpressionStatement(InvocationExpression(addTypeAliasMethod,
                    ArgumentList(SeparatedList(new[] { Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(type.Alias))), Argument(TypeOfExpression(type.Type)) })))));
            }

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
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetCopierTypeName(this ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = CopierGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetActivatorTypeName(this ISerializableTypeDescription type)
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