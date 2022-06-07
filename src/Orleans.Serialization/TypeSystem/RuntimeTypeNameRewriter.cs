using System;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Rewrites <see cref="TypeSpec"/> graphs.
    /// </summary>
    internal static class RuntimeTypeNameRewriter
    {
        /// <summary>
        /// Signature for a delegate which rewrites a <see cref="QualifiedType"/>.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="state">The state provided to the rewriter method.</param>
        /// <returns>The rewritten qualified type.</returns>
        public delegate QualifiedType Rewriter<TState>(in QualifiedType input, ref TState state);

        /// <summary>
        /// Rewrites a <see cref="TypeSpec"/> using the provided rewriter delegate.
        /// </summary>
        public static TypeSpec Rewrite<TState>(TypeSpec input, Rewriter<TState> rewriter, ref TState state)
        {
            var result = ApplyInner(input, null, rewriter, ref state);
            if (result.Assembly is not null)
            {
                // If the rewriter bubbled up an assembly, add it here. 
                return new AssemblyQualifiedTypeSpec(result.Type, result.Assembly);
            }

            return result.Type;
        }

        private static (TypeSpec Type, string Assembly) ApplyInner<TState>(TypeSpec input, string assemblyName, Rewriter<TState> replaceFunc, ref TState state) =>
            // A type's assembly is passed downwards through the graph, and modifications to the assembly (from the user-provided delegate) flow upwards.
            input switch
            {
                ConstructedGenericTypeSpec type => HandleGeneric(type, assemblyName, replaceFunc, ref state),
                NamedTypeSpec type => HandleNamedType(type, assemblyName, replaceFunc, ref state),
                AssemblyQualifiedTypeSpec type => HandleAssembly(type, assemblyName, replaceFunc, ref state),
                ArrayTypeSpec type => HandleArray(type, assemblyName, replaceFunc, ref state),
                PointerTypeSpec type => HandlePointer(type, assemblyName, replaceFunc, ref state),
                ReferenceTypeSpec type => HandleReference(type, assemblyName, replaceFunc, ref state),
                null => throw new ArgumentNullException(nameof(input)),
                _ => throw new NotSupportedException($"Argument of type {input.GetType()} is nut supported"),
            };

        private static (TypeSpec Type, string Assembly) HandleGeneric<TState>(ConstructedGenericTypeSpec type, string assemblyName, Rewriter<TState> replaceTypeName, ref TState state)
        {
            var (unconstructed, replacementAssembly) = ApplyInner(type.UnconstructedType, assemblyName, replaceTypeName, ref state);

            var newArguments = new TypeSpec[type.Arguments.Length];
            var didChange = false;
            for (var i = 0; i < type.Arguments.Length; i++)
            {
                // Generic type parameters do not inherit the assembly of the generic type.
                var args = ApplyInner(type.Arguments[i], null, replaceTypeName, ref state);

                if (args.Assembly is not null)
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

        private static (TypeSpec Type, string Assembly) HandleNamedType<TState>(NamedTypeSpec type, string assemblyName, Rewriter<TState> replaceTypeName, ref TState state)
        {
            var nsQualified = type.GetNamespaceQualifiedName();
            var names = replaceTypeName(new QualifiedType(assembly: assemblyName, type: nsQualified), ref state);

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
            else if (type.ContainingType is not null)
            {
                // Give the user an opportunity to change the parent, including the assembly.
                var replacementParent = ApplyInner(type.ContainingType, assemblyName, replaceTypeName, ref state);
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

        private static (TypeSpec Type, string Assembly) HandleAssembly<TState>(AssemblyQualifiedTypeSpec type, string assemblyName, Rewriter<TState> replaceTypeName, ref TState state)
        {
            var replacement = ApplyInner(type.Type, type.Assembly, replaceTypeName, ref state);

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

        private static (TypeSpec Type, string Assembly) HandleArray<TState>(ArrayTypeSpec type, string assemblyName, Rewriter<TState> replaceTypeName, ref TState state)
        {
            var element = ApplyInner(type.ElementType, assemblyName, replaceTypeName, ref state);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new ArrayTypeSpec(element.Type, type.Dimensions), element.Assembly);
        }

        private static (TypeSpec Type, string Assembly) HandleReference<TState>(ReferenceTypeSpec type, string assemblyName, Rewriter<TState> replaceTypeName, ref TState state)
        {
            var element = ApplyInner(type.ElementType, assemblyName, replaceTypeName, ref state);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new ReferenceTypeSpec(element.Type), element.Assembly);
        }

        private static (TypeSpec Type, string Assembly) HandlePointer<TState>(PointerTypeSpec type, string assemblyName, Rewriter<TState> replaceTypeName, ref TState state)
        {
            var element = ApplyInner(type.ElementType, assemblyName, replaceTypeName, ref state);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new PointerTypeSpec(element.Type), element.Assembly);
        }
    }
}