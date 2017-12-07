using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Utilities;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal static class FeaturePopulatorGenerator
    {
        private const string NamespaceName = "OrleansGeneratedCode";
        private const string ClassSuffix = "FeaturePopulator";

        public static (List<AttributeListSyntax>, List<MemberDeclarationSyntax>) GenerateSyntax(Assembly targetAssembly, FeatureDescriptions features)
        {
            var attributes = new List<AttributeListSyntax>();
            var members = new List<MemberDeclarationSyntax>();
            var className = CodeGeneratorCommon.ClassPrefix + Guid.NewGuid().ToString("N").Substring(0, 10) + ClassSuffix;

            // Generate a class for populating the metadata.
            var classSyntax = SF.ClassDeclaration(className)
                                .AddBaseListTypes(
                                    SF.SimpleBaseType(typeof(IFeaturePopulator<GrainInterfaceFeature>).GetTypeSyntax()),
                                    SF.SimpleBaseType(typeof(IFeaturePopulator<GrainClassFeature>).GetTypeSyntax()),
                                    SF.SimpleBaseType(typeof(IFeaturePopulator<SerializerFeature>).GetTypeSyntax()))
                                .AddModifiers(SF.Token(SyntaxKind.InternalKeyword), SF.Token(SyntaxKind.SealedKeyword))
                                .AddMembers(GeneratePopulateMethod(features.GrainInterfaces), GeneratePopulateMethod(features.GrainClasses), GeneratePopulateMethod(features.Serializers))
                                .AddAttributeLists(SF.AttributeList(SF.SingletonSeparatedList(CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax())));

            var namespaceSyntax = SF.NamespaceDeclaration(NamespaceName.ToIdentifierName()).AddMembers(classSyntax);
            members.Add(namespaceSyntax);

            // Generate an assembly-level attribute with an instance of that class.
            var attribute = SF.AttributeList(
                SF.AttributeTargetSpecifier(SF.Token(SyntaxKind.AssemblyKeyword)),
                SF.SingletonSeparatedList(
                    SF.Attribute(typeof(FeaturePopulatorAttribute).GetNameSyntax())
                      .AddArgumentListArguments(SF.AttributeArgument(SF.TypeOfExpression(SF.ParseTypeName(NamespaceName + "." + className))))));
            attributes.Add(attribute);

            return (attributes, members);
        }

        private static MemberDeclarationSyntax GeneratePopulateMethod(List<GrainInterfaceDescription> grains)
        {
            var interfaceMethod = TypeUtils.Method((IFeaturePopulator<GrainInterfaceFeature> _) => _.Populate(default(GrainInterfaceFeature)));
            var featureParameter = interfaceMethod.GetParameters()[0].Name.ToIdentifierName();

            var grainsMember = TypeUtils.Member((GrainInterfaceFeature feature) => feature.Interfaces);
            var addMethod = TypeUtils.Method((IList<GrainInterfaceMetadata> _) => _.Add(default(GrainInterfaceMetadata)));

            var bodyStatements = new List<StatementSyntax>();
            foreach (var metadata in grains)
            {
                var newMetadataExpression = SF.ObjectCreationExpression(typeof(GrainInterfaceMetadata).GetTypeSyntax())
                                              .AddArgumentListArguments(
                                                  SF.Argument(SF.TypeOfExpression(metadata.Interface)),
                                                  SF.Argument(SF.TypeOfExpression(metadata.Reference)),
                                                  SF.Argument(SF.TypeOfExpression(metadata.Invoker)),
                                                  SF.Argument(SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(metadata.InterfaceId))));
                bodyStatements.Add(
                    SF.ExpressionStatement(
                        SF.InvocationExpression(featureParameter.Member(grainsMember.Name).Member(addMethod.Name))
                          .AddArgumentListArguments(
                              SF.Argument(newMetadataExpression))));
            }

            return interfaceMethod.GetDeclarationSyntax().AddBodyStatements(bodyStatements.ToArray());
        }

        private static MemberDeclarationSyntax GeneratePopulateMethod(List<GrainClassDescription> grains)
        {
            var interfaceMethod = TypeUtils.Method((IFeaturePopulator<GrainClassFeature> _) => _.Populate(default(GrainClassFeature)));
            var featureParameter = interfaceMethod.GetParameters()[0].Name.ToIdentifierName();

            var grainsMember = TypeUtils.Member((GrainClassFeature feature) => feature.Classes);
            var addMethod = TypeUtils.Method((IList<GrainClassMetadata> _) => _.Add(default(GrainClassMetadata)));

            var bodyStatements = new List<StatementSyntax>();
            foreach (var metadata in grains)
            {
                var newMetadataExpression = SF.ObjectCreationExpression(typeof(GrainClassMetadata).GetTypeSyntax())
                                              .AddArgumentListArguments(
                                                  SF.Argument(SF.TypeOfExpression(metadata.ClassType)));
                bodyStatements.Add(
                    SF.ExpressionStatement(
                        SF.InvocationExpression(featureParameter.Member(grainsMember.Name).Member(addMethod.Name))
                          .AddArgumentListArguments(
                              SF.Argument(newMetadataExpression))));
            }

            return interfaceMethod.GetDeclarationSyntax().AddBodyStatements(bodyStatements.ToArray());
        }

        private static MemberDeclarationSyntax GeneratePopulateMethod(SerializationTypeDescriptions typeDescriptions)
        {
            var interfaceMethod = TypeUtils.Method((IFeaturePopulator<SerializerFeature> _) => _.Populate(default(SerializerFeature)));
            var featureParameter = interfaceMethod.GetParameters()[0].Name.ToIdentifierName();
            var bodyStatements = new List<StatementSyntax>();

            var addSerializerTypeMethod = TypeUtils.Method((SerializerFeature _) => _.AddSerializerType(default(Type), default(Type)));
            foreach (var serializerType in typeDescriptions.SerializerTypes)
            {
                bodyStatements.Add(
                    SF.ExpressionStatement(
                        SF.InvocationExpression(featureParameter.Member(addSerializerTypeMethod.Name))
                          .AddArgumentListArguments(
                              SF.Argument(SF.TypeOfExpression(serializerType.Target)),
                              SF.Argument(SF.TypeOfExpression(serializerType.Serializer)))));
            }

            var addKnownType = TypeUtils.Method((SerializerFeature _) => _.AddKnownType(default(string), default(string)));
            foreach (var knownType in typeDescriptions.KnownTypes)
            {
                bodyStatements.Add(
                    SF.ExpressionStatement(
                        SF.InvocationExpression(featureParameter.Member(addKnownType.Name))
                          .AddArgumentListArguments(
                              SF.Argument(knownType.Type.GetLiteralExpression()),
                              SF.Argument(knownType.TypeKey.GetLiteralExpression()))));
            }

            return interfaceMethod.GetDeclarationSyntax().AddBodyStatements(bodyStatements.ToArray());
        }
    }
}