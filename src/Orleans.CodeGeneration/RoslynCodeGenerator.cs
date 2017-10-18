using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.ApplicationParts;
using Orleans.CodeGenerator.Utilities;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Utilities;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Implements a code generator using the Roslyn C# compiler.
    /// </summary>
    public class RoslynCodeGenerator : ISourceCodeGenerator
    {
        private const string SerializerNamespacePrefix = "OrleansGeneratedCode";

        /// <summary>
        /// The logger.
        /// </summary>
        private readonly Logger Logger;

        /// <summary>
        /// The serializer generation manager.
        /// </summary>
        private readonly SerializerGenerationManager serializerGenerationManager;

        private readonly TypeCollector typeCollector = new TypeCollector();
        private readonly HashSet<string> knownTypes;

        /// <summary>
        /// Initializes a new instance of the <see cref="RoslynCodeGenerator"/> class.
        /// </summary>
        /// <param name="serializationManager">The serialization manager.</param>
        /// <param name="loggerFactory">logger factory to use</param>
        public RoslynCodeGenerator(SerializationManager serializationManager, ApplicationPartManager applicationPartManager, ILoggerFactory loggerFactory)
        {
            this.knownTypes = GetKnownTypes();
            this.serializerGenerationManager = new SerializerGenerationManager(serializationManager, loggerFactory);
            Logger = new LoggerWrapper<RoslynCodeGenerator>(loggerFactory);

            HashSet<string> GetKnownTypes()
            {
                var serializerFeature = applicationPartManager.CreateAndPopulateFeature<SerializerFeature>();

                var result = new HashSet<string>();
                foreach (var kt in serializerFeature.KnownTypes) result.Add(kt.Type);
                foreach (var serializer in serializerFeature.SerializerTypes)
                {
                    result.Add(RuntimeTypeNameFormatter.Format(serializer.Target));
                    result.Add(RuntimeTypeNameFormatter.Format(serializer.Serializer));
                }

                foreach (var serializer in serializerFeature.SerializerDelegates)
                {
                    result.Add(RuntimeTypeNameFormatter.Format(serializer.Target));
                }

                return result;
            }
        }
        
        /// <summary>
        /// Generates source code for the provided assembly.
        /// </summary>
        /// <param name="input">
        /// The assembly to generate source for.
        /// </param>
        /// <returns>
        /// The generated source.
        /// </returns>
        public string GenerateSourceForAssembly(Assembly input)
        {
            if (input.GetCustomAttribute<GeneratedCodeAttribute>() != null
                || input.GetCustomAttribute<SkipCodeGenerationAttribute>() != null)
            {
                return string.Empty;
            }

            var generated = GenerateForAssemblies(input, false);
            if (generated.Syntax == null)
            {
                return string.Empty;
            }

            return CodeGeneratorCommon.GenerateSourceCode(CodeGeneratorCommon.AddGeneratedCodeAttribute(generated));
        }
        
        /// <summary>
        /// Generates a syntax tree for the provided assemblies.
        /// </summary>
        /// <param name="assemblies">The assemblies to generate code for.</param>
        /// <param name="runtime">Whether or not runtime code generation is being performed.</param>
        /// <returns>The generated syntax tree.</returns>
        private GeneratedSyntax GenerateForAssemblies(Assembly targetAssembly, bool runtime)
        {
            var grainInterfaces = new List<GrainInterfaceDescription>();
            var grainClasses = new List<GrainClassDescription>();
            var serializationTypes = new SerializationTypeDescriptions();
            
            var members = new List<MemberDeclarationSyntax>();

            // Expand the list of included assemblies and types.
            var (includedTypes, assemblies) = this.GetIncludedTypes(targetAssembly, runtime);

            if (Logger.IsVerbose)
            {
                Logger.Verbose(
                    "Generating code for assemblies: {0}",
                    string.Join(", ", assemblies.Select(_ => _.FullName)));
            }

            var serializerNamespaceMembers = new List<MemberDeclarationSyntax>();
            var serializerNamespaceName = $"{SerializerNamespacePrefix}{targetAssembly?.GetName().Name.GetHashCode():X}";

            // Group the types by namespace and generate the required code in each namespace.
            foreach (var group in includedTypes.GroupBy(_ => CodeGeneratorCommon.GetGeneratedNamespace(_)))
            {
                var namespaceMembers = new List<MemberDeclarationSyntax>();
                var namespaceName = group.Key;
                foreach (var type in group)
                {
                    // Skip generated classes.
                    if (type.GetCustomAttribute<GeneratedCodeAttribute>() != null) continue;

                    // Every type which is encountered must be considered for serialization.
                    void OnEncounteredType(Type encounteredType)
                    {
                        // If a type was encountered which can be accessed, process it for serialization.
                        this.typeCollector.RecordEncounteredType(type);
                        this.serializerGenerationManager.RecordTypeToGenerate(encounteredType, targetAssembly);
                    }

                    if (Logger.IsVerbose2)
                    {
                        Logger.Verbose2("Generating code for: {0}", type.GetParseableName());
                    }

                    if (GrainInterfaceUtils.IsGrainInterface(type))
                    {
                        if (Logger.IsVerbose2)
                        {
                            Logger.Verbose2(
                                "Generating GrainReference and MethodInvoker for {0}",
                                type.GetParseableName());
                        }

                        GrainInterfaceUtils.ValidateInterfaceRules(type);

                        var referenceTypeName = GrainReferenceGenerator.GetGeneratedClassName(type);
                        var invokerTypeName = GrainMethodInvokerGenerator.GetGeneratedClassName(type);
                        namespaceMembers.Add(GrainReferenceGenerator.GenerateClass(type, referenceTypeName, OnEncounteredType));
                        namespaceMembers.Add(GrainMethodInvokerGenerator.GenerateClass(type, invokerTypeName));
                        var genericTypeSuffix = GetGenericTypeSuffix(type.GetGenericArguments().Length);
                        grainInterfaces.Add(
                            new GrainInterfaceDescription
                            {
                                Interface = type.GetTypeSyntax(includeGenericParameters: false),
                                Reference = SF.ParseTypeName(namespaceName + '.' + referenceTypeName + genericTypeSuffix),
                                Invoker = SF.ParseTypeName(namespaceName + '.' + invokerTypeName + genericTypeSuffix),
                                InterfaceId = GrainInterfaceUtils.GetGrainInterfaceId(type)
                            });
                    }
                    
                    if (TypeUtils.IsConcreteGrainClass(type))
                    {
                        grainClasses.Add(
                            new GrainClassDescription
                            {
                                ClassType = type.GetTypeSyntax(includeGenericParameters: false)
                            });
                    }

                    // Generate serializers.
                    var first = true;
                    while (this.serializerGenerationManager.GetNextTypeToProcess(out var toGen))
                    {
                        if (first)
                        {
                            Logger.Info("ClientGenerator - Generating serializer classes for types:");
                            first = false;
                        }
                        
                        Logger.Info(
                            "\ttype " + toGen.FullName + " in namespace " + toGen.Namespace
                            + " defined in Assembly " + toGen.GetTypeInfo().Assembly.GetName());

                        if (Logger.IsVerbose2)
                        {
                            Logger.Verbose2(
                                "Generating serializer for type {0}",
                                toGen.GetParseableName());
                        }

                        var generatedSerializerName = SerializerGenerator.GetGeneratedClassName(toGen);
                        serializerNamespaceMembers.Add(SerializerGenerator.GenerateClass(generatedSerializerName, toGen, OnEncounteredType));
                        var qualifiedSerializerName = serializerNamespaceName + '.' + generatedSerializerName + GetGenericTypeSuffix(toGen.GetGenericArguments().Length);
                        serializationTypes.SerializerTypes.Add(
                            new SerializerTypeDescription
                            {
                                Serializer = SF.ParseTypeName(qualifiedSerializerName),
                                Target = toGen.GetTypeSyntax(includeGenericParameters: false)
                            });
                    }
                }

                if (namespaceMembers.Count == 0)
                {
                    if (Logger.IsVerbose)
                    {
                        Logger.Verbose2("Skipping namespace: {0}", namespaceName);
                    }

                    continue;
                }

                members.Add(CreateNamespace(namespaceName, namespaceMembers));
            }

            // Add all generated serializers to their own namespace.
            members.Add(CreateNamespace(serializerNamespaceName, serializerNamespaceMembers));
            
            // Add serialization metadata for the types which were encountered.
            this.AddSerializationTypes(serializationTypes, targetAssembly);

            // Generate metadata directives for all of the relevant types.
            var (attributeDeclarations, memberDeclarations) = FeaturePopulatorGenerator.GenerateSyntax(targetAssembly, grainInterfaces, grainClasses, serializationTypes);
            members.AddRange(memberDeclarations);
            
            var compilationUnit = SF.CompilationUnit().AddAttributeLists(attributeDeclarations.ToArray()).AddMembers(members.ToArray());
            return new GeneratedSyntax
            {
                SourceAssemblies = assemblies,
                Syntax = compilationUnit
            };

            string GetGenericTypeSuffix(int numParams)
            {
                if (numParams == 0) return string.Empty;
                return '<' + new string(',', numParams - 1) + '>';
            }

            NamespaceDeclarationSyntax CreateNamespace(string namespaceName, IEnumerable<MemberDeclarationSyntax> namespaceMembers)
            {
                return
                    SF.NamespaceDeclaration(SF.ParseName(namespaceName))
                      .AddUsings(
                          TypeUtils.GetNamespaces(typeof(GrainExtensions), typeof(IntrospectionExtensions))
                                   .Select(_ => SF.UsingDirective(SF.ParseName(_)))
                                   .ToArray())
                      .AddMembers(namespaceMembers.ToArray());
            }
        }

        private (List<Type>, List<Assembly>) GetIncludedTypes(Assembly targetAssembly, bool runtime)
        {
            // Include assemblies which are marked as included.
            var knownAssemblyAttributes = new Dictionary<Assembly, KnownAssemblyAttribute>();
            var knownAssemblies = new HashSet<Assembly> {targetAssembly};
            foreach (var attribute in targetAssembly.GetCustomAttributes<KnownAssemblyAttribute>())
            {
                knownAssemblyAttributes[attribute.Assembly] = attribute;
                knownAssemblies.Add(attribute.Assembly);
            }

            // Get types from assemblies which reference Orleans and are not generated assemblies.
            var includedTypes = new HashSet<Type>();
            foreach (var assembly in knownAssemblies)
            {
                var considerAllTypesForSerialization = knownAssemblyAttributes.TryGetValue(assembly, out var knownAssemblyAttribute)
                                                       && knownAssemblyAttribute.TreatTypesAsSerializable;

                foreach (var attribute in assembly.GetCustomAttributes<ConsiderForCodeGenerationAttribute>())
                {
                    this.ConsiderType(attribute.Type, runtime, targetAssembly, includedTypes, considerForSerialization: true);
                    if (attribute.ThrowOnFailure && !this.serializerGenerationManager.IsTypeRecorded(attribute.Type))
                    {
                        throw new CodeGenerationException(
                            $"Found {attribute.GetType().Name} for type {attribute.Type.GetParseableName()}, but code"
                            + " could not be generated. Ensure that the type is accessible.");
                    }
                }

                foreach (var type in TypeUtils.GetDefinedTypes(assembly, this.Logger))
                {
                    this.typeCollector.RecordEncounteredType(type);
                    var considerForSerialization = considerAllTypesForSerialization || type.IsSerializable;
                    this.ConsiderType(type.AsType(), runtime, targetAssembly, includedTypes, considerForSerialization);
                }
            }

            return (includedTypes.ToList(), knownAssemblies.ToList());
        }

        /// <summary>
        /// Adds serialization type descriptions from <paramref name="types"/> to <paramref name="serializationTypes"/>.
        /// </summary>
        /// <param name="serializationTypes">The serialization type descriptions.</param>
        /// <param name="targetAssembly">The target assembly for generated code.</param>
        /// <param name="types">The types.</param>
        private void AddSerializationTypes(SerializationTypeDescriptions serializationTypes, Assembly targetAssembly)
        {
            // Only types which exist in assemblies referenced by the target assembly can be referenced.
            var references = new HashSet<string>(targetAssembly.GetReferencedAssemblies().Select(asm => asm.Name));

            bool IsAssemblyReferenced(Type type)
            {
                // If the target doesn't reference this type's assembly, it cannot reference a type within that assembly.
                var asmName = type.Assembly.GetName().Name;
                if (type.Assembly != targetAssembly)
                {
                    if (!references.Contains(asmName)) return false;
                    if (!type.IsSerializable) return false;
                }

                return true;
            }

            // Visit all types in other assemblies for serialization metadata.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!references.Contains(assembly.GetName().Name)) continue;
                foreach (var type in TypeUtils.GetDefinedTypes(assembly, this.Logger))
                {
                    this.typeCollector.RecordEncounteredType(type);
                }
            }

            // Returns true if a type can be accessed from source and false otherwise.
            bool IsAccessibleType(Type type)
            {
                var accessible = !type.IsGenericParameter;

                if (type.IsSpecialName)
                {
                    accessible = false;
                }

                if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
                {
                    accessible = false;
                }

                // Obsolete types can be accessed, however obsolete types which have IsError set cannot.
                var obsoleteAttr = type.GetCustomAttribute<ObsoleteAttribute>();
                if (obsoleteAttr != null && obsoleteAttr.IsError)
                {
                    accessible = false;
                }

                if (!TypeUtilities.IsAccessibleFromAssembly(type, targetAssembly))
                {
                    accessible = false;
                }

                return accessible;
            }

            foreach (var type in this.typeCollector.EncounteredTypes)
            {
                if (!IsAssemblyReferenced(type)) continue;
                
                if (type.GetCustomAttribute<GeneratedCodeAttribute>() != null) continue;

                var qualifiedTypeName = RuntimeTypeNameFormatter.Format(type);
                if (this.knownTypes.Contains(qualifiedTypeName)) continue;

                serializationTypes.KnownTypes.Add(new KnownTypeDescription
                {
                    Type = qualifiedTypeName,
                    TypeKey = type.OrleansTypeKeyString()
                });

                if (!IsAccessibleType(type)) continue;

                var typeSyntax = type.GetTypeSyntax(includeGenericParameters: false);
                var serializerAttributes = type.GetCustomAttributes<SerializerAttribute>().ToList();
                if (serializerAttributes.Count > 0)
                {
                    // Account for serializer types.
                    foreach (var serializerAttribute in serializerAttributes)
                    {
                        if (!IsAccessibleType(serializerAttribute.TargetType)) continue;
                        serializationTypes.SerializerTypes.Add(
                            new SerializerTypeDescription
                            {
                                Serializer = typeSyntax,
                                Target = serializerAttribute.TargetType.GetTypeSyntax(includeGenericParameters: false)
                            });
                    }
                }
                else
                {
                    // Account for self-serializing types.
                    SerializationManager.GetSerializationMethods(type, out var copier, out var serializer, out var deserializer);
                    if (copier != null || serializer != null || deserializer != null)
                    {
                        serializationTypes.SerializerTypes.Add(
                            new SerializerTypeDescription
                            {
                                Serializer = typeSyntax,
                                Target = typeSyntax
                            });
                    }
                }
            }
        }

        private void ConsiderType(
            Type type,
            bool runtime,
            Assembly targetAssembly,
            ISet<Type> includedTypes,
            bool considerForSerialization = false)
        {
            // The module containing the serializer.
            var typeInfo = type.GetTypeInfo();

            // If a type was encountered which can be accessed and is marked as [Serializable], process it for serialization.
            if (considerForSerialization)
            {
                this.RecordType(type, targetAssembly, includedTypes);
            }
            
            // Consider generic arguments to base types and implemented interfaces for code generation.
            this.ConsiderGenericBaseTypeArguments(typeInfo, targetAssembly, includedTypes);
            this.ConsiderGenericInterfacesArguments(typeInfo, targetAssembly, includedTypes);
            
            // Include grain interface types.
            if (GrainInterfaceUtils.IsGrainInterface(type))
            {
                // If code generation is being performed at runtime, the interface must be accessible to the generated code.
                if (!runtime || TypeUtilities.IsAccessibleFromAssembly(type, targetAssembly))
                {
                    if (Logger.IsVerbose2) Logger.Verbose2("Will generate code for: {0}", type.GetParseableName());

                    includedTypes.Add(type);
                }
            }

            if (TypeUtils.IsConcreteGrainClass(type))
            {
                includedTypes.Add(type);
            }
        }

        private void RecordType(Type type, Assembly targetAssembly, ISet<Type> includedTypes)
        {
            this.typeCollector.RecordEncounteredType(type);
            if (this.serializerGenerationManager.RecordTypeToGenerate(type, targetAssembly))
            {
                includedTypes.Add(type);
            }
        }

        private void ConsiderGenericBaseTypeArguments(
            TypeInfo typeInfo,
            Assembly targetAssembly,
            ISet<Type> includedTypes)
        {
            if (typeInfo.BaseType == null) return;
            if (!typeInfo.BaseType.IsConstructedGenericType) return;

            foreach (var type in typeInfo.BaseType.GetGenericArguments())
            {
                this.RecordType(type, targetAssembly, includedTypes);
            }
        }

        private void ConsiderGenericInterfacesArguments(
            TypeInfo typeInfo,
            Assembly targetAssembly,
            ISet<Type> includedTypes)
        {
            var interfaces = typeInfo.GetInterfaces().Where(x => x.IsConstructedGenericType);
            foreach (var type in interfaces.SelectMany(v => v.GetTypeInfo().GetGenericArguments()))
            {
                this.RecordType(type, targetAssembly, includedTypes);
            }
        }
    }
}
