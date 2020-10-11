using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Orleans.CodeGenerator.Compatibility;
using Orleans.CodeGenerator.Utilities;

namespace Orleans.CodeGenerator.Analysis
{
    internal class CompilationAnalyzer
    {
        private readonly ILogger log;
        private readonly WellKnownTypes wellKnownTypes;
        private readonly INamedTypeSymbol serializableAttribute;
        private readonly INamedTypeSymbol knownBaseTypeAttribute;
        private readonly INamedTypeSymbol knownAssemblyAttribute;
        private readonly INamedTypeSymbol considerForCodeGenerationAttribute;
        private readonly Compilation compilation;

        /// <summary>
        /// Assemblies whose declared types are all considered serializable.
        /// </summary>
        private readonly HashSet<IAssemblySymbol> assembliesWithForcedSerializability = new HashSet<IAssemblySymbol>();

        /// <summary>
        /// Types whose sub-types are all considered serializable.
        /// </summary>
        private readonly HashSet<INamedTypeSymbol> knownBaseTypes = new HashSet<INamedTypeSymbol>();

        /// <summary>
        /// Types which were observed in a grain interface.
        /// </summary>
        private readonly HashSet<ITypeSymbol> dependencyTypes = new HashSet<ITypeSymbol>();

        private readonly HashSet<INamedTypeSymbol> grainInterfacesToProcess = new HashSet<INamedTypeSymbol>();
        private readonly HashSet<INamedTypeSymbol> grainClassesToProcess = new HashSet<INamedTypeSymbol>();
        private readonly HashSet<INamedTypeSymbol> serializationTypesToProcess = new HashSet<INamedTypeSymbol>();
        private readonly HashSet<INamedTypeSymbol> fieldOfSerializableType = new HashSet<INamedTypeSymbol>();

        public CompilationAnalyzer(ILogger log, WellKnownTypes wellKnownTypes, Compilation compilation)
        {
            this.log = log;
            this.wellKnownTypes = wellKnownTypes;
            this.serializableAttribute = wellKnownTypes.SerializableAttribute;
            this.knownBaseTypeAttribute = wellKnownTypes.KnownBaseTypeAttribute;
            this.knownAssemblyAttribute = wellKnownTypes.KnownAssemblyAttribute;
            this.considerForCodeGenerationAttribute = wellKnownTypes.ConsiderForCodeGenerationAttribute;
            this.compilation = compilation;
        }

        public HashSet<INamedTypeSymbol> CodeGenerationRequiredTypes { get; } = new HashSet<INamedTypeSymbol>();

        /// <summary>
        /// All assemblies referenced by this compilation.
        /// </summary>
        public HashSet<IAssemblySymbol> ReferencedAssemblies = new HashSet<IAssemblySymbol>();

        /// <summary>
        /// Assemblies which should be excluded from code generation (eg, because they already contain generated code).
        /// </summary>
        public HashSet<IAssemblySymbol> AssembliesExcludedFromCodeGeneration = new HashSet<IAssemblySymbol>();

        /// <summary>
        /// Assemblies which should be excluded from metadata generation.
        /// </summary>
        public HashSet<IAssemblySymbol> AssembliesExcludedFromMetadataGeneration = new HashSet<IAssemblySymbol>();

        public HashSet<IAssemblySymbol> KnownAssemblies { get; } = new HashSet<IAssemblySymbol>();
        public HashSet<INamedTypeSymbol> KnownTypes { get; } = new HashSet<INamedTypeSymbol>();

        public (IEnumerable<INamedTypeSymbol> grainClasses, IEnumerable<INamedTypeSymbol> grainInterfaces, IEnumerable<INamedTypeSymbol> types) GetTypesToProcess() =>
            (this.grainClassesToProcess, this.grainInterfacesToProcess, this.GetSerializationTypesToProcess());

        private IEnumerable<INamedTypeSymbol> GetSerializationTypesToProcess()
        {
            var done = new HashSet<INamedTypeSymbol>();
            var remaining = new HashSet<INamedTypeSymbol>();
            while (done.Count != this.serializationTypesToProcess.Count)
            {
                remaining.Clear();
                foreach (var type in this.serializationTypesToProcess)
                {
                    if (done.Add(type)) remaining.Add(type);
                }

                foreach (var type in remaining)
                {
                    yield return type;
                }
            }
        }

        public bool IsSerializable(INamedTypeSymbol type)
        {
            var result = false;
            if (type.IsSerializable || type.HasAttribute(this.serializableAttribute))
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogTrace($"Type {type} has [Serializable] attribute.");
                result = true;
            }

            if (!result && this.assembliesWithForcedSerializability.Contains(type.ContainingAssembly))
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogTrace($"Type {type} is declared in an assembly in which all types are considered serializable");
                result = true;
            }

            if (!result && this.KnownTypes.Contains(type))
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogTrace($"Type {type} is a known type");
                result = true;
            }

            if (!result && this.dependencyTypes.Contains(type))
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogTrace($"Type {type} was discovered on a grain method signature or in another serializable type");
                result = true;
            }

            if (!result)
            {
                for (var current = type; current != null; current = current.BaseType)
                {
                    if (!knownBaseTypes.Contains(current)) continue;

                    if (log.IsEnabled(LogLevel.Debug)) log.LogTrace($"Type {type} has a known base type");
                    result = true;
                }
            }

            if (!result)
            {
                foreach (var iface in type.AllInterfaces)
                {
                    if (!knownBaseTypes.Contains(iface)) continue;

                    if (log.IsEnabled(LogLevel.Debug)) log.LogTrace($"Type {type} has a known base interface");
                    result = true;
                }
            }

            if (!result && this.fieldOfSerializableType.Contains(type))
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogTrace($"Type {type} is used in a field of another serializable type");
                result = true;
            }

            if (!result)
            {
                if (log.IsEnabled(LogLevel.Trace)) log.LogTrace($"Type {type} is not serializable");
            }
            else
            {
                foreach (var field in type.GetInstanceMembers<IFieldSymbol>())
                {
                    ExpandGenericArguments(field.Type);
                }

                void ExpandGenericArguments(ITypeSymbol typeSymbol)
                {
                    if (typeSymbol is INamedTypeSymbol named && this.fieldOfSerializableType.Add(named))
                    {
                        InspectType(named);
                        foreach (var param in named.GetHierarchyTypeArguments())
                        {
                            ExpandGenericArguments(param);
                        }
                    }
                }
            }

            return result;
        }

        public bool IsFromKnownAssembly(ITypeSymbol type) => this.KnownAssemblies.Contains(type.OriginalDefinition.ContainingAssembly);

        private void InspectGrainInterface(INamedTypeSymbol type)
        {
            this.serializationTypesToProcess.Add(type);
            this.grainInterfacesToProcess.Add(type);
            foreach (var method in type.GetInstanceMembers<IMethodSymbol>())
            {
                var awaitable = IsAwaitable(method);

                if (!awaitable && !method.ReturnsVoid)
                {
                    var message = $"Grain interface {type} has method {method} which returns a non-awaitable type {method.ReturnType}."
                        + " All grain interface methods must return awaitable types."
                        + $" Did you mean to return Task<{method.ReturnType}>?";
                    this.log.LogError(message);
                    throw new InvalidOperationException(message);
                }

                if (method.ReturnType is INamedTypeSymbol returnType)
                {
                    foreach (var named in ExpandType(returnType).OfType<INamedTypeSymbol>())
                    {
                        this.AddDependencyType(named);
                        this.serializationTypesToProcess.Add(named);
                    }
                }

                foreach (var param in method.Parameters)
                {
                    if (param.Type is INamedTypeSymbol parameterType)
                    {
                        foreach (var named in ExpandType(parameterType).OfType<INamedTypeSymbol>())
                        {
                            this.AddDependencyType(named);
                            this.serializationTypesToProcess.Add(named);
                        }
                    }
                }
            }

            bool IsAwaitable(IMethodSymbol method)
            {
                foreach (var member in method.ReturnType.GetMembers("GetAwaiter"))
                {
                    if (member.IsStatic) continue;
                    if (member is IMethodSymbol m && HasZeroParameters(m))
                    {
                        return true;
                    }
                }

                return false;

                bool HasZeroParameters(IMethodSymbol m) => m.Parameters.Length == 0 && m.TypeParameters.Length == 0;
            }
        }

        private static IEnumerable<ITypeSymbol> ExpandType(ITypeSymbol symbol)
        {
            return ExpandTypeInternal(symbol, new HashSet<ITypeSymbol>());
            IEnumerable<ITypeSymbol> ExpandTypeInternal(ITypeSymbol s, HashSet<ITypeSymbol> emitted)
            {
                if (!emitted.Add(s)) yield break;
                yield return s;
                switch (s)
                {
                    case IArrayTypeSymbol array:
                        foreach (var t in ExpandTypeInternal(array.ElementType, emitted)) yield return t;
                        break;
                    case INamedTypeSymbol named:
                        foreach (var p in named.TypeArguments)
                            foreach (var t in ExpandTypeInternal(p, emitted))
                                yield return t;
                        break;
                }

                if (s.BaseType != null)
                {
                    foreach (var t in ExpandTypeInternal(s.BaseType, emitted)) yield return t;
                }
            }
        }

        public void InspectType(INamedTypeSymbol type)
        {
            if (type.HasAttribute(knownBaseTypeAttribute)) this.AddKnownBaseType(type);
            if (this.wellKnownTypes.IsGrainInterface(type)) this.InspectGrainInterface(type);
            if (this.wellKnownTypes.IsGrainClass(type)) this.grainClassesToProcess.Add(type);
            this.serializationTypesToProcess.Add(type);
        }

        public void Analyze()
        {
            foreach (var reference in this.compilation.References)
            {
                if (!(this.compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm)) continue;
                this.ReferencedAssemblies.Add(asm);
            }

            // Recursively all assemblies considered known from the inspected assembly.
            ExpandKnownAssemblies(compilation.Assembly);

            // Add all types considered known from each known assembly.
            ExpandKnownTypes(this.KnownAssemblies);

            this.ExpandAssembliesWithGeneratedCode();

            void ExpandKnownAssemblies(IAssemblySymbol asm)
            {
                if (!this.KnownAssemblies.Add(asm))
                {
                    return;
                }

                if (!asm.GetAttributes(this.knownAssemblyAttribute, out var attrs)) return;

                foreach (var attr in attrs)
                {
                    var param = attr.ConstructorArguments.First();
                    if (param.Kind != TypedConstantKind.Type)
                    {
                        throw new ArgumentException($"Unrecognized argument type in attribute [{attr.AttributeClass.Name}({param.ToCSharpString()})]");
                    }

                    var type = (ITypeSymbol)param.Value;
                    if (log.IsEnabled(LogLevel.Debug)) log.LogDebug($"Known assembly {type.ContainingAssembly} from assembly {asm}");

                    // Check if the attribute has the TreatTypesAsSerializable property set.
                    var prop = attr.NamedArguments.FirstOrDefault(a => a.Key.Equals("TreatTypesAsSerializable")).Value;
                    if (prop.Type != null)
                    {
                        var treatAsSerializable = (bool)prop.Value;
                        if (treatAsSerializable)
                        {
                            // When checking if a type in this assembly is serializable, always respond that it is.
                            this.AddAssemblyWithForcedSerializability(asm);
                        }
                    }

                    // Recurse on the assemblies which the type was declared in.
                    ExpandKnownAssemblies(type.OriginalDefinition.ContainingAssembly);
                }
            }

            void ExpandKnownTypes(IEnumerable<IAssemblySymbol> asm)
            {
                foreach (var a in asm)
                {
                    if (!a.GetAttributes(this.considerForCodeGenerationAttribute, out var attrs)) continue;

                    foreach (var attr in attrs)
                    {
                        var typeParam = attr.ConstructorArguments.First();
                        if (typeParam.Kind != TypedConstantKind.Type)
                        {
                            throw new ArgumentException($"Unrecognized argument type in attribute [{attr.AttributeClass.Name}({typeParam.ToCSharpString()})]");
                        }

                        var type = (INamedTypeSymbol)typeParam.Value;
                        this.KnownTypes.Add(type);

                        var throwOnFailure = false;
                        var throwOnFailureParam = attr.ConstructorArguments.ElementAtOrDefault(2);
                        if (throwOnFailureParam.Type != null)
                        {
                            throwOnFailure = (bool)throwOnFailureParam.Value;
                            if (throwOnFailure) this.CodeGenerationRequiredTypes.Add(type);
                        }

                        if (log.IsEnabled(LogLevel.Debug)) log.LogDebug($"Known type {type}, Throw on failure: {throwOnFailure}");
                    }
                }
            }
        }

        private void ExpandAssembliesWithGeneratedCode()
        {
            foreach (var asm in this.ReferencedAssemblies)
            {
                if (!asm.GetAttributes(this.wellKnownTypes.OrleansCodeGenerationTargetAttribute, out var attrs)) continue;

                this.AssembliesExcludedFromMetadataGeneration.Add(asm);
                this.AssembliesExcludedFromCodeGeneration.Add(asm);

                foreach (var attr in attrs)
                {
                    var assemblyName = attr.ConstructorArguments[0].Value as string;
                    bool metadataOnly;
                    if (attr.ConstructorArguments.Length >= 2 && attr.ConstructorArguments[1].Value is bool val)
                    {
                        metadataOnly = val;
                    }
                    else
                    {
                        metadataOnly = false;
                    }

                    if (string.IsNullOrWhiteSpace(assemblyName)) continue;
                    foreach (var candidate in this.ReferencedAssemblies)
                    {
                        bool hasGeneratedCode;
                        if (string.Equals(assemblyName, candidate.Identity.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            hasGeneratedCode = true;
                        }
                        else if (string.Equals(assemblyName, candidate.Identity.GetDisplayName()))
                        {
                            hasGeneratedCode = true;
                        }
                        else if (string.Equals(assemblyName, candidate.Identity.GetDisplayName(fullKey: true)))
                        {
                            hasGeneratedCode = true;
                        }
                        else
                        {
                            hasGeneratedCode = false;
                        }

                        if (hasGeneratedCode)
                        {
                            this.AssembliesExcludedFromMetadataGeneration.Add(candidate);
                            if (!metadataOnly)
                            {
                                this.AssembliesExcludedFromCodeGeneration.Add(candidate);
                            }

                            break;
                        }
                    }
                }
            }
        }

        public void AddAssemblyWithForcedSerializability(IAssemblySymbol asm) => this.assembliesWithForcedSerializability.Add(asm);

        public void AddKnownBaseType(INamedTypeSymbol type)
        {
            if (log.IsEnabled(LogLevel.Debug)) this.log.LogDebug($"Added known base type {type}");
            this.knownBaseTypes.Add(type);
        }

        public void AddDependencyType(ITypeSymbol type)
        {
            if (!(type is INamedTypeSymbol named)) return;
            if (named.IsGenericType && !named.IsUnboundGenericType)
            {
                var unbound = named.ConstructUnboundGenericType();
                if (SymbolEqualityComparer.Default.Equals(unbound, this.wellKnownTypes.Task_1))
                {
                    return;
                }
            }

            this.dependencyTypes.Add(type);
        }
    }
}