using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal class SerializableTypeDescription : ISerializableTypeDescription
    {
        private readonly LibraryTypes _libraryTypes;
        private TypeSyntax _typeSyntax;
        private TypeSyntax _baseTypeSyntax;

        public SerializableTypeDescription(SemanticModel semanticModel, INamedTypeSymbol type, IEnumerable<IMemberDescription> members, LibraryTypes libraryTypes)
        {
            Type = type;
            Members = members.ToList();
            SemanticModel = semanticModel;
            _libraryTypes = libraryTypes;

            var t = type;
            Accessibility accessibility = t.DeclaredAccessibility;
            while (t is not null)
            {
                if ((int)t.DeclaredAccessibility < (int)accessibility)
                {
                    accessibility = t.DeclaredAccessibility;
                }

                t = t.ContainingType;
            }

            Accessibility = accessibility;
            TypeParameters = new();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tp in type.GetAllTypeParameters())
            {
                var tpName = GetTypeParameterName(names, tp);
                TypeParameters.Add((tpName, tp));
            }

            SerializationHooks = new();
            if (type.GetAttributes(libraryTypes.SerializationCallbacksAttribute, out var hookAttributes))
            {
                foreach (var hookAttribute in hookAttributes)
                {
                    var hookType = (INamedTypeSymbol)hookAttribute.ConstructorArguments[0].Value;
                    SerializationHooks.Add(hookType);
                }
            }

            if (TryGetActivatorConstructor(type, _libraryTypes, out var constructorParameters))
            {
                HasActivatorConstructor = true;
                ActivatorConstructorParameters = constructorParameters;
            }

            static bool TryGetActivatorConstructor(INamedTypeSymbol type, LibraryTypes libraryTypes, out List<TypeSyntax> parameters)
            {
                parameters = null;
                if (type.IsAbstract)
                {
                    return false;
                }

                foreach (var constructor in type.GetAllMembers<IMethodSymbol>())
                {
                    if (constructor.MethodKind != MethodKind.Constructor || constructor.DeclaredAccessibility == Accessibility.Private || constructor.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    if (constructor.HasAttribute(libraryTypes.GeneratedActivatorConstructorAttribute))
                    {
                        foreach (var parameter in constructor.Parameters)
                        {
                            var argumentType = parameter.Type.ToTypeSyntax();
                            (parameters ??= new()).Add(argumentType);
                        }

                        break;
                    }
                }

                return parameters is not null;
            }

            static string GetTypeParameterName(HashSet<string> names, ITypeParameterSymbol tp)
            {
                var count = 0;
                var result = tp.Name;
                while (names.Contains(result))
                {
                    result = $"{tp.Name}_{++count}";
                }

                names.Add(result);
                return result.EscapeIdentifier();
            }
        }

        private INamedTypeSymbol Type { get; }

        public Accessibility Accessibility { get; }

        public TypeSyntax TypeSyntax => _typeSyntax ??= Type.ToTypeSyntax();

        public TypeSyntax BaseTypeSyntax => _baseTypeSyntax ??= BaseType.ToTypeSyntax();

        public bool HasComplexBaseType => !IsValueType &&
                                          Type.BaseType != null &&
                                          Type.BaseType.SpecialType != SpecialType.System_Object;

        public INamedTypeSymbol BaseType => Type.EnumUnderlyingType ?? Type.BaseType;

        public string Namespace => Type.GetNamespaceAndNesting();

        public string GeneratedNamespace => Namespace switch
        {
            { Length: > 0 } ns => $"{CodeGenerator.CodeGeneratorName}.{ns}",
            _ => CodeGenerator.CodeGeneratorName
        };

        public string Name => Type.Name;

        public bool IsValueType => Type.IsValueType;
        public bool IsSealedType => Type.IsSealed;
        public bool IsEnumType => Type.EnumUnderlyingType != null;

        public bool IsGenericType => Type.IsGenericType;

        public List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters { get; }

        public List<IMemberDescription> Members { get; }
        public SemanticModel SemanticModel { get; }
        public List<TypeSyntax> ActivatorConstructorParameters { get; }

        public bool IsEmptyConstructable
        {
            get
            {
                if (Type.Constructors.Length == 0)
                {
                    return true;
                }

                foreach (var ctor in Type.Constructors)
                {
                    if (ctor.Parameters.Length != 0)
                    {
                        continue;
                    }

                    switch (ctor.DeclaredAccessibility)
                    {
                        case Accessibility.Public:
                            return true;
                    }
                }

                return false;
            }
        }

        public bool HasActivatorConstructor { get; }

        public bool IsPartial
        {
            get
            {
                foreach (var reference in Type.DeclaringSyntaxReferences)
                {
                    var syntax = reference.GetSyntax();
                    if (syntax is TypeDeclarationSyntax typeDeclaration && typeDeclaration.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public bool UseActivator => Type.HasAttribute(_libraryTypes.UseActivatorAttribute) || !IsEmptyConstructable || HasActivatorConstructor;

        public bool TrackReferences => !IsValueType && !Type.HasAttribute(_libraryTypes.SuppressReferenceTrackingAttribute);
        public bool OmitDefaultMemberValues => Type.HasAttribute(_libraryTypes.OmitDefaultMemberValuesAttribute);

        public List<INamedTypeSymbol> SerializationHooks { get; }

        public bool IsImmutable => IsEnumType || Type.HasAnyAttribute(_libraryTypes.ImmutableAttributes);

        public ExpressionSyntax GetObjectCreationExpression(LibraryTypes libraryTypes)
        {
            if (IsValueType)
            {
                return DefaultExpression(TypeSyntax);
            }

            var instanceConstructors = Type.InstanceConstructors;
            var isConstructible = false;
            if (!instanceConstructors.IsDefaultOrEmpty)
            {
                foreach (var ctor in instanceConstructors)
                {
                    if (ctor.Parameters.IsDefaultOrEmpty)
                    {
                        if (ctor.IsImplicitlyDeclared || ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
                        {
                            isConstructible = true;
                        }

                        break;
                    }
                }
            }

            if (isConstructible)
            {
                return ObjectCreationExpression(TypeSyntax).WithArgumentList(ArgumentList());
            }
            else
            {
                return CastExpression(
                    TypeSyntax,
                    InvocationExpression(libraryTypes.FormatterServices.ToTypeSyntax().Member("GetUninitializedObject"))
                        .AddArgumentListArguments(
                            Argument(TypeOfExpression(TypeSyntax))));
            }
        }
    }
}