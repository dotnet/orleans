using System;

namespace Orleans.Utilities
{
    /// <summary>
    /// Rewrites <see cref="TypeSpec"/> graphs.
    /// </summary>
    internal static class RuntimeTypeNameRewriter
    {
        /// <summary>
        /// Rewrites a <see cref="TypeSpec"/> using the provided rewriter delegate.
        /// </summary>
        public static TypeSpec Rewrite(TypeSpec input, Func<QualifiedType, QualifiedType> rewriter)
        {
            var result = ApplyInner(input, null, rewriter);
            if (result.Assembly is object)
            {
                // If the rewriter bubbled up an assembly, add it here. 
                return new AssemblyQualifiedTypeSpec(result.Type, result.Assembly);
            }

            return result.Type;
        }

        private static (TypeSpec Type, string Assembly) ApplyInner(TypeSpec input, string assemblyName, Func<QualifiedType, QualifiedType> replaceFunc)
        {
            // A type's assembly is passed downwards through the graph, and modifications to the assembly (from the user-provided delegate) flow upwards.
            switch (input)
            {
                case ConstructedGenericTypeSpec type:
                    return HandleGeneric(type, assemblyName, replaceFunc);
                case NamedTypeSpec type:
                    return HandleNamedType(type, assemblyName, replaceFunc);
                case AssemblyQualifiedTypeSpec type:
                    return HandleAssembly(type, assemblyName, replaceFunc);
                case ArrayTypeSpec type:
                    return HandleArray(type, assemblyName, replaceFunc);
                case PointerTypeSpec type:
                    return HandlePointer(type, assemblyName, replaceFunc);
                case ReferenceTypeSpec type:
                    return HandleReference(type, assemblyName, replaceFunc);
                case null:
                    throw new ArgumentNullException(nameof(input));
                default:
                    throw new NotSupportedException($"Argument of type {input.GetType()} is nut supported");
            }
        }

        private static (TypeSpec Type, string Assembly) HandleGeneric(ConstructedGenericTypeSpec type, string assemblyName, Func<QualifiedType, QualifiedType> replaceTypeName)
        {
            var (unconstructed, replacementAssembly) = ApplyInner(type.UnconstructedType, assemblyName, replaceTypeName);

            var newArguments = new TypeSpec[type.Arguments.Length];
            var didChange = false;
            for (var i = 0; i < type.Arguments.Length; i++)
            {
                // Generic type parameters do not inherit the assembly of the generic type.
                var args = ApplyInner(type.Arguments[i], null, replaceTypeName);

                if (args.Assembly is object)
                {
                    newArguments[i] = new AssemblyQualifiedTypeSpec(args.Type, args.Assembly);
                }
                else
                {
                    newArguments[i] = args.Type;
                }

                didChange |= !ReferenceEquals(newArguments[i], type.Arguments[i]);
            }

            if (ReferenceEquals(type.UnconstructedType, unconstructed) && !didChange)
            {
                return (type, replacementAssembly);
            }

            return (new ConstructedGenericTypeSpec((NamedTypeSpec)unconstructed, newArguments), replacementAssembly);
        }

        private static (TypeSpec Type, string Assembly) HandleNamedType(NamedTypeSpec type, string assemblyName, Func<QualifiedType, QualifiedType> replaceTypeName)
        {
            var nsQualified = type.GetNamespaceQualifiedName();
            var names = replaceTypeName(new QualifiedType(assembly: assemblyName, type: nsQualified));

            if (!string.Equals(nsQualified, names.Type, StringComparison.Ordinal))
            {
                // Change the type name and potentially the assembly.
                var resultType = RuntimeTypeNameParser.Parse(names.Type);
                if (!(resultType is NamedTypeSpec))
                {
                    throw new InvalidOperationException($"Replacement type name, \"{names.Type}\", can not deviate from the original type structure of the input, \"{nsQualified}\"");
                }

                return (resultType, names.Assembly);
            }
            else if (!string.Equals(assemblyName, names.Assembly, StringComparison.Ordinal))
            {
                // Only change the assembly;
                return (type, names.Assembly);
            }
            else if (type.ContainingType is object)
            {
                // Give the user an opportunity to change the parent, including the assembly.
                var replacementParent = ApplyInner(type.ContainingType, assemblyName, replaceTypeName);
                if (ReferenceEquals(replacementParent.Type, type.ContainingType))
                {
                    // No change to the type.
                    return (type, replacementParent.Assembly);
                }

                // The parent type changed.
                var typedReplacement = (NamedTypeSpec)replacementParent.Type;
                return (new NamedTypeSpec(typedReplacement, type.Name, type.Arity), replacementParent.Assembly);
            }
            else
            {
                return (type, names.Assembly);
            }
        }

        private static (TypeSpec Type, string Assembly) HandleAssembly(AssemblyQualifiedTypeSpec type, string assemblyName, Func<QualifiedType, QualifiedType> replaceTypeName)
        {
            var replacement = ApplyInner(type.Type, type.Assembly, replaceTypeName);

            // Assembly name changes never bubble up past the assembly qualifier node.
            if (string.IsNullOrWhiteSpace(replacement.Assembly))
            {
                // Remove the assembly qualification
                return (replacement.Type, assemblyName);
            }
            else if (!string.Equals(replacement.Assembly, type.Assembly) || !ReferenceEquals(replacement.Type, type.Type))
            {
                // Update the assembly or the type.
                return (new AssemblyQualifiedTypeSpec(replacement.Type, replacement.Assembly), assemblyName);
            }

            // No update.
            return (type, assemblyName);
        }

        private static (TypeSpec Type, string Assembly) HandleArray(ArrayTypeSpec type, string assemblyName, Func<QualifiedType, QualifiedType> replaceTypeName)
        {
            var element = ApplyInner(type.ElementType, assemblyName, replaceTypeName);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new ArrayTypeSpec(element.Type, type.Dimensions), element.Assembly);
        }

        private static (TypeSpec Type, string Assembly) HandleReference(ReferenceTypeSpec type, string assemblyName, Func<QualifiedType, QualifiedType> replaceTypeName)
        {
            var element = ApplyInner(type.ElementType, assemblyName, replaceTypeName);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new ReferenceTypeSpec(element.Type), element.Assembly);
        }

        private static (TypeSpec Type, string Assembly) HandlePointer(PointerTypeSpec type, string assemblyName, Func<QualifiedType, QualifiedType> replaceTypeName)
        {
            var element = ApplyInner(type.ElementType, assemblyName, replaceTypeName);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new PointerTypeSpec(element.Type), element.Assembly);
        }
    }

    public readonly struct QualifiedType
    {
        public QualifiedType(string assembly, string type)
        {
            this.Assembly = assembly;
            this.Type = type;
        }

        public void Deconstruct(out string assembly, out string type)
        {
            assembly = this.Assembly;
            type = this.Type;
        }

        public static implicit operator QualifiedType((string Assembly, string Type) args) => new QualifiedType(args.Assembly, args.Type);

        public string Assembly { get; }
        public string Type { get; }
    }
}
