using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using Microsoft.CodeAnalysis.CSharp;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Describes an invokable method on a proxy interface.
    /// </summary>
    [DebuggerDisplay("{Method} (from {ProxyInterface})")]
    internal class ProxyMethodDescription : IEquatable<ProxyMethodDescription>
    {
        private readonly GeneratedInvokableDescription _originalInvokable;
        public static ProxyMethodDescription Create(
            ProxyInterfaceDescription proxyInterface,
            GeneratedInvokableDescription generatedInvokable,
            IMethodSymbol method,
            bool hasCollision) => new(proxyInterface, generatedInvokable, method, hasCollision);

        private ProxyMethodDescription(ProxyInterfaceDescription proxyInterface, GeneratedInvokableDescription generatedInvokable, IMethodSymbol method, bool hasCollision)
        {
            _originalInvokable = generatedInvokable;
            Method = method;
            ProxyInterface = proxyInterface;
            HasCollision = hasCollision;

            TypeParameters = new List<(string Name, ITypeParameterSymbol Parameter)>();
            MethodTypeParameters = new List<(string Name, ITypeParameterSymbol Parameter)>();

            TypeParametersWithArguments = Method.ContainingType.GetAllTypeParameters().Zip(method.ContainingType.GetAllTypeArguments(), (a, b) => (a, b)).ToImmutableArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (typeParameter, typeArgument) in TypeParametersWithArguments)
            {
                var tpName = GetTypeParameterName(names, typeParameter);
                TypeParameters.Add((tpName, typeParameter));
            }

            foreach (var typeParameter in Method.TypeParameters)
            {
                var tpName = GetTypeParameterName(names, typeParameter);
                TypeParameters.Add((tpName, typeParameter));
                MethodTypeParameters.Add((tpName, typeParameter));
            }

            TypeParameterSubstitutions = new(SymbolEqualityComparer.Default);
            foreach (var (name, parameter) in TypeParameters)
            {
                TypeParameterSubstitutions[parameter] = name;
            }

            foreach (var (parameter, arg) in TypeParametersWithArguments)
            {
                TypeParameterSubstitutions[parameter] = arg.ToDisplayName();
            }

            GeneratedInvokable = new ConstructedGeneratedInvokableDescription(generatedInvokable, this);
            static string GetTypeParameterName(HashSet<string> names, ITypeParameterSymbol typeParameter)
            {
                var count = 0;
                var result = typeParameter.Name;
                while (names.Contains(result))
                {
                    result = $"{typeParameter.Name}_{++count}";
                }

                names.Add(result);
                return result.EscapeIdentifier();
            }
        }

        public CodeGenerator CodeGenerator => InvokableMethod.CodeGenerator;
        public InvokableMethodDescription InvokableMethod => _originalInvokable.MethodDescription;
        public ConstructedGeneratedInvokableDescription GeneratedInvokable { get; }
        public ProxyInterfaceDescription ProxyInterface { get; }

        public bool HasCollision { get; }   
        public IMethodSymbol Method { get; }
        public InvokableMethodId InvokableId { get; }
        public List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters { get; }
        public List<(string Name, ITypeParameterSymbol Parameter)> MethodTypeParameters { get; }
        public ImmutableArray<(ITypeParameterSymbol Parameter, ITypeSymbol Argument)> TypeParametersWithArguments { get; }
        public Dictionary<ITypeParameterSymbol, string> TypeParameterSubstitutions { get; }

        /// <summary>
        /// Mapping of method return types to invokable base type. The code generator will create a derived type with the method arguments as fields.
        /// </summary>
        public IReadOnlyDictionary<INamedTypeSymbol, INamedTypeSymbol> InvokableBaseTypes => InvokableMethod.InvokableBaseTypes;
        public InvokableMethodId InvokableKey => InvokableMethod.Key;
        public List<(string, TypedConstant)> CustomInitializerMethods => InvokableMethod.CustomInitializerMethods;
        public string GeneratedMethodId => InvokableMethod.GeneratedMethodId;
        public string MethodId => InvokableMethod.MethodId;
        public bool HasAlias => InvokableMethod.HasAlias;
        public long? ResponseTimeoutTicks => InvokableMethod.ResponseTimeoutTicks;

        public override int GetHashCode() => ProxyInterface.GetHashCode() * 17 ^ InvokableMethod.GetHashCode();
        public bool Equals(ProxyMethodDescription other) => other is not null && InvokableMethod.Key.Equals(other.InvokableKey) && ProxyInterface.Equals(other.ProxyInterface);
        public override bool Equals(object other) => other is ProxyMethodDescription imd && Equals(imd);

        internal sealed class ConstructedGeneratedInvokableDescription : ISerializableTypeDescription
        {
            private TypeSyntax _typeSyntax;
            private TypeSyntax _baseTypeSyntax;
            private readonly GeneratedInvokableDescription _invokableDescription;
            private readonly ProxyMethodDescription _proxyMethod;

            public ConstructedGeneratedInvokableDescription(GeneratedInvokableDescription invokableDescription, ProxyMethodDescription proxyMethod)
            {
                _invokableDescription = invokableDescription;
                _proxyMethod = proxyMethod;
                Members = new List<IMemberDescription>(invokableDescription.Members.Count);
                var proxyMethodParameters = proxyMethod.Method.Parameters;
                foreach (var member in invokableDescription.Members.OfType<InvokableGenerator.MethodParameterFieldDescription>())
                {
                    Members.Add(new InvokableGenerator.MethodParameterFieldDescription(
                        proxyMethod.CodeGenerator,
                        proxyMethodParameters[member.ParameterOrdinal],
                        member.FieldName,
                        member.FieldId,
                        proxyMethod.TypeParameterSubstitutions));
                }
            }

            public Accessibility Accessibility => _invokableDescription.Accessibility;
            public TypeSyntax TypeSyntax => _typeSyntax ??= CreateTypeSyntax();
            public TypeSyntax OpenTypeSyntax => _invokableDescription.OpenTypeSyntax;
            public bool HasComplexBaseType => BaseType is { SpecialType: not SpecialType.System_Object };
            public bool IncludePrimaryConstructorParameters => false;
            public INamedTypeSymbol BaseType => _invokableDescription.BaseType;
            public TypeSyntax BaseTypeSyntax => _baseTypeSyntax ??= BaseType.ToTypeSyntax(_proxyMethod.TypeParameterSubstitutions);
            public string Namespace => GeneratedNamespace;
            public string GeneratedNamespace => _invokableDescription.GeneratedNamespace;
            public string Name => _invokableDescription.Name;
            public bool IsValueType => _invokableDescription.IsValueType;
            public bool IsSealedType => _invokableDescription.IsSealedType;
            public bool IsAbstractType => _invokableDescription.IsAbstractType;
            public bool IsEnumType => _invokableDescription.IsEnumType;
            public bool IsGenericType => TypeParameters.Count > 0;
            public List<IMemberDescription> Members { get; }
            public Compilation Compilation => MethodDescription.CodeGenerator.Compilation;
            public bool IsEmptyConstructable => ActivatorConstructorParameters is not { Count: > 0 };
            public bool UseActivator => ActivatorConstructorParameters is { Count: > 0 };
            public bool TrackReferences => _invokableDescription.TrackReferences;
            public bool OmitDefaultMemberValues => _invokableDescription.OmitDefaultMemberValues;
            public List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters => _proxyMethod.TypeParameters;
            public List<INamedTypeSymbol> SerializationHooks => _invokableDescription.SerializationHooks;
            public bool IsShallowCopyable => _invokableDescription.IsShallowCopyable;
            public bool IsUnsealedImmutable => _invokableDescription.IsUnsealedImmutable;
            public bool IsImmutable => _invokableDescription.IsImmutable;
            public bool IsExceptionType => _invokableDescription.IsExceptionType;
            public List<TypeSyntax> ActivatorConstructorParameters => _invokableDescription.ActivatorConstructorParameters;
            public bool HasActivatorConstructor => UseActivator;
            public string ReturnValueInitializerMethod => _invokableDescription.ReturnValueInitializerMethod;

            public InvokableMethodDescription MethodDescription => _invokableDescription.MethodDescription;

            public ExpressionSyntax GetObjectCreationExpression() => ObjectCreationExpression(TypeSyntax, ArgumentList(), null);

            private TypeSyntax CreateTypeSyntax()
            {
                var simpleName = InvokableGenerator.GetSimpleClassName(MethodDescription);
                var subs = _proxyMethod.TypeParameterSubstitutions;
                return (TypeParameters, Namespace) switch
                {
                    ({ Count: > 0 }, { Length: > 0 }) => QualifiedName(ParseName(Namespace), GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => IdentifierName(subs[p.Parameter])))))),
                    ({ Count: > 0 }, _) => GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => IdentifierName(subs[p.Parameter]))))),
                    (_, { Length: > 0 }) => QualifiedName(ParseName(Namespace), IdentifierName(simpleName)),
                    _ => IdentifierName(simpleName),
                };
            }
        }
    }
}