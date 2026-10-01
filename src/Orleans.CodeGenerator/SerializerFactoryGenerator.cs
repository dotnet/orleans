using System.Text;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class SerializerFactoryGenerator
{
    internal sealed class Registration(ITypeSymbol type)
    {
        public ITypeSymbol Type { get; } = type;
        public string Codec { get; set; } = "";
        public string Copier { get; set; } = "";
        public string CodecConstruction { get; set; } = "";
        public string CopierConstruction { get; set; } = "";
        public List<ITypeSymbol> Dependencies { get; } = [];
        public ISerializableTypeDescription? Model { get; set; }
        public ITypeSymbol? ResponseResult { get; set; }
    }

    internal sealed class Graph(IReadOnlyDictionary<ITypeSymbol, Registration> registrations, string configurationStatements)
    {
        public IReadOnlyDictionary<ITypeSymbol, Registration> Registrations { get; } = registrations;
        public string ConfigurationStatements { get; } = configurationStatements;
    }

    internal sealed record Failure(ITypeSymbol Type, string Reason);

    internal static bool TryCreate(
        IGeneratorServices services,
        IEnumerable<ITypeSymbol> roots,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Graph? graph,
        [NotNullWhen(false)] out Failure? failure,
        bool useDefaultFactories = false)
    {
        graph = null;
        failure = null;
        var registrations = new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default);
        var pending = new Queue<ITypeSymbol>(roots);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Dequeue().WithNullableAnnotation(NullableAnnotation.None);
            if (registrations.ContainsKey(type)) continue;
            if (registrations.Count >= 1024)
            {
                failure = new(type, "the dependency graph exceeds 1024 closed types; declare a finite serialization graph");
                return false;
            }
            var registration = new Registration(type);
            if (Describe(registration, services) is { } reason)
            {
                failure = new(type, reason);
                return false;
            }
            registrations.Add(type, registration);
            foreach (var dependency in registration.Dependencies) pending.Enqueue(dependency);
        }

        var serializerGenerator = new SerializerGenerator(services);
        var copierGenerator = new CopierGenerator(services);
        var result = new StringBuilder();
        foreach (var registration in registrations.Values)
        {
            ResolveResponseImplementations(registration);
        }

        var addService = useDefaultFactories ? "AddDefaultSerializerService" : "AddSerializerService";
        var addSerializer = useDefaultFactories ? "AddDefaultSerializer" : "AddSerializer";

        foreach (var registration in registrations.Values.OrderBy(value => Name(value.Type), StringComparer.Ordinal))
        {
            var typeName = Name(registration.Type);
            if (registration.Model is { } model)
            {
                var codecDeclaration = serializerGenerator.Generate(model);
                var copierDeclaration = copierGenerator.GenerateCopier(model, new());
                registration.CodecConstruction = ConstructGenerated(registration.Codec, codecDeclaration);
                registration.CopierConstruction = copierDeclaration is null
                    ? $"new {registration.Copier}()"
                    : ConstructGenerated(registration.Copier, copierDeclaration);
            }
            else if (registration.Dependencies.Count > 0)
            {
                var codecArguments = new List<string>();
                var copierArguments = new List<string>();
                foreach (var dependency in registration.Dependencies)
                {
                    var target = registrations[dependency.WithNullableAnnotation(NullableAnnotation.None)];
                    var cyclic = Reaches(target, registration.Type, registrations, new(SymbolEqualityComparer.Default));
                    codecArguments.Add(cyclic && registration.ResponseResult is not null
                        ? $"caller => {Resolve(target.Codec, "caller")}"
                        : cyclic
                        ? $"CreateCodecHolder<{Name(dependency)}>(provider)"
                        : Resolve(target.Codec));
                    copierArguments.Add(cyclic && registration.ResponseResult is not null
                        ? $"caller => {Resolve(target.Copier, "caller")}"
                        : cyclic
                        ? $"CreateCopierHolder<{Name(dependency)}>(provider)"
                        : Resolve(target.Copier));
                }

                registration.CodecConstruction = $"new {registration.Codec}({string.Join(", ", codecArguments)})";
                registration.CopierConstruction = $"new {registration.Copier}({string.Join(", ", copierArguments)})";
            }

            result.Append("options.").Append(addService).Append('<').Append(registration.Codec).Append(">(static provider => ")
                .Append(registration.CodecConstruction).AppendLine(");");
            result.Append("options.").Append(addService).Append('<').Append(registration.Copier).Append(">(static provider => ")
                .Append(registration.CopierConstruction).AppendLine(");");
            result.Append("options.").Append(addSerializer).Append('<').Append(typeName).Append(">(static provider => ")
                .Append(Resolve(registration.Codec)).Append(", static provider => ").Append(Resolve(registration.Copier)).AppendLine(");");
            result.Append("options.AddAllowedType(typeof(").Append(typeName).AppendLine("));");
            if (!useDefaultFactories && registration.Type is INamedTypeSymbol named)
            {
                AppendTypeMetadata(result, named, services.LibraryTypes);
            }
        }

        graph = new Graph(registrations, result.ToString());
        return true;

        void ResolveResponseImplementations(Registration registration)
        {
            if (registration.ResponseResult is not { } resultType || registration.Codec.Length > 0)
            {
                return;
            }

            var target = registrations[resultType.WithNullableAnnotation(NullableAnnotation.None)];
            ResolveResponseImplementations(target);
            registration.Codec = $"global::Orleans.Serialization.Invocation.PooledResponseCodec<{Name(resultType)}, {target.Codec}>";
            registration.Copier = $"global::Orleans.Serialization.Invocation.PooledResponseCopier<{Name(resultType)}, {target.Copier}>";
        }
    }

    private static string? Describe(Registration registration, IGeneratorServices services)
    {
        var type = registration.Type;
        if (type is INamedTypeSymbol { IsUnboundGenericType: true } || ContainsTypeParameter(type))
            return "use a closed type with explicit generic arguments";
        if (services.LibraryTypes.StaticCodecs.FindByUnderlyingType(type) is { } primitive
            && type.SpecialType != SpecialType.System_Object)
        {
            if (primitive.CodecType.InstanceConstructors.All(constructor => constructor.Parameters.Length > 0))
                return "this built-in type requires runtime service configuration";
            var copier = services.LibraryTypes.StaticCopiers.FindByUnderlyingType(type);
            if (copier is null && !services.LibraryTypes.IsShallowCopyable(type))
                return "this built-in type requires a statically registered deep copier";
            registration.Codec = Name(primitive.CodecType);
            registration.Copier = copier is { }
                ? Name(copier.CopierType)
                : $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>";
            registration.CodecConstruction = $"new {registration.Codec}()";
            registration.CopierConstruction = $"new {registration.Copier}()";
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (!array.IsSZArray) return "use a single-dimensional zero-based array";
            registration.Codec = $"global::Orleans.Serialization.Codecs.ArrayCodec<{Name(array.ElementType)}>";
            registration.Copier = $"global::Orleans.Serialization.Codecs.ArrayCopier<{Name(array.ElementType)}>";
            registration.Dependencies.Add(array.ElementType);
            return null;
        }

        if (type is not INamedTypeSymbol named || named.IsRefLikeType)
            return "use a serializable class, struct, enum, or supported collection";
        if (named.ContainingType is { IsGenericType: true })
            return "use a model declared outside a generic containing type";
        var definition = named.OriginalDefinition.ToDisplayString();
        if (definition == "Orleans.Serialization.Invocation.Response<TResult>")
        {
            registration.ResponseResult = named.TypeArguments[0];
            registration.Dependencies.Add(named.TypeArguments[0]);
            return null;
        }

        var collection = definition switch
        {
            "System.Collections.Generic.List<T>" => "List",
            "System.Collections.Generic.Dictionary<TKey, TValue>" => "Dictionary",
            "System.Nullable<T>" => "Nullable",
            _ => null
        };
        if (collection is not null)
        {
            var arguments = string.Join(", ", named.TypeArguments.Select(Name));
            registration.Codec = $"global::Orleans.Serialization.Codecs.{collection}Codec<{arguments}>";
            registration.Copier = $"global::Orleans.Serialization.Codecs.{collection}Copier<{arguments}>";
            registration.Dependencies.AddRange(named.TypeArguments);
            return null;
        }

        if (!named.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute))
            return "apply GenerateSerializerAttribute to the model, or use List<T>, Dictionary<TKey, TValue>, Nullable<T>, or T[]";
        if (named.IsAbstract || named.TypeKind == TypeKind.Interface)
            return "declare concrete model types";
        ISerializableTypeDescription? model;
        try
        {
            model = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, named);
        }
        catch (OrleansGeneratorDiagnosticAnalysisException exception)
        {
            return exception.Diagnostic.GetMessage();
        }
        if (model is null) return "the model has no generated serialization implementation";
        if (model.HasComplexBaseType || model.UseActivator || model.SerializationHooks.Count > 0)
            return "this context supports models with default construction, an object base, and generated member serialization";
        foreach (var member in model.Members)
        {
            if (!member.IsSerializable && !member.IsCopyable) continue;
            var generatedMember = new SerializerGenerator.SerializableMember(services, member);
            if (generatedMember.GetGetterFieldDescription() is { InitializationSyntax: not null }
                || generatedMember.GetSetterFieldDescription() is { InitializationSyntax: not null })
                return $"serialized member '{member.Symbol.Name}' requires statically generated field accessor support";
            registration.Dependencies.Add(member.Type);
        }

        var argumentsSuffix = named.IsGenericType
            ? $"<{string.Join(", ", named.TypeArguments.Select(Name))}>"
            : "";
        var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(named);
        registration.Codec = $"global::{generatedNamespace}.{SerializerGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        var definitionModel = named.IsGenericType
            ? SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, named.OriginalDefinition)!
            : model;
        registration.Copier = definitionModel.IsShallowCopyable
            ? $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>"
            : $"global::{generatedNamespace}.{CopierGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        registration.Model = definitionModel;
        return null;
    }

    private static string ConstructGenerated(string name, ClassDeclarationSyntax declaration)
    {
        var constructor = declaration.Members.OfType<ConstructorDeclarationSyntax>().SingleOrDefault();
        var arguments = constructor?.ParameterList.Parameters.Select(parameter =>
            parameter.Type!.ToString().EndsWith(".ICodecProvider", StringComparison.Ordinal)
                || parameter.Type.ToString() == "ICodecProvider"
                ? "provider"
                : Resolve(parameter.Type.ToString())) ?? [];
        return $"new {name}({string.Join(", ", arguments)})";
    }

    private static void AppendTypeMetadata(StringBuilder result, INamedTypeSymbol type, LibraryTypes library)
    {
        var openType = type.ToOpenTypeSyntax().ToString();
        if (GeneratedCodeUtilities.GetAlias(library, type) is { } alias)
        {
            result.Append("options.WellKnownTypeAliases.TryAdd(").Append(alias.GetLiteralExpression())
                .Append(", typeof(").Append(openType).AppendLine("));");
        }

        if (GeneratedCodeUtilities.GetId(library, type) is { } id)
        {
            result.Append("options.WellKnownTypeIds.TryAdd(").Append(id).Append("U, typeof(").Append(openType).AppendLine("));");
        }

        if (type.GetAttribute(library.CompoundTypeAliasAttribute) is { ConstructorArguments.Length: 1 } compound)
        {
            var components = compound.ConstructorArguments[0].Values;
            foreach (var componentType in components.Select(component => component.Value).OfType<ITypeSymbol>())
            {
                result.Append("options.AddAllowedType(typeof(").Append(Name(componentType)).AppendLine("));");
            }

            result.Append("options.CompoundTypeAliases");
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index].Value switch
                {
                    string value => value.GetLiteralExpression().ToString(),
                    ITypeSymbol symbol => $"typeof({Name(symbol)})",
                    _ => throw new InvalidOperationException("Compound aliases contain type or string components.")
                };
                result.Append(".Add(").Append(component);
                if (index == components.Length - 1)
                    result.Append(", typeof(").Append(openType).Append(')');
                result.Append(')');
            }

            result.AppendLine(";");
        }
    }

    private static bool Reaches(Registration current, ITypeSymbol target,
        Dictionary<ITypeSymbol, Registration> registrations, HashSet<ITypeSymbol> visited)
    {
        if (SymbolEqualityComparer.Default.Equals(current.Type, target)) return true;
        if (!visited.Add(current.Type)) return false;
        return current.Dependencies.Any(dependency =>
            Reaches(registrations[dependency.WithNullableAnnotation(NullableAnnotation.None)], target, registrations, visited));
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
        => type is ITypeParameterSymbol
            || type is INamedTypeSymbol named && named.TypeArguments.Any(ContainsTypeParameter)
            || type is IArrayTypeSymbol array && ContainsTypeParameter(array.ElementType);

    private static string Resolve(string type, string caller = "null!")
        => $"global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<{type}>({caller}, provider)";

    private static string Name(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

}
