using System;
using System.Linq;
using System.Text;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Represents a type.
    /// </summary>
    public abstract class TypeSpec
    {
        /// <summary>
        /// Formats this instance in a way that can be parsed by <see cref="RuntimeTypeNameParser"/>.
        /// </summary>
        public abstract string Format();
    }

    /// <summary>
    /// Represents a pointer (*) type.
    /// </summary>
    public class PointerTypeSpec : TypeSpec
    {
        public PointerTypeSpec(TypeSpec elementType)
        {
            if (elementType is null)
            {
                throw new ArgumentNullException(nameof(elementType));
            }

            ElementType = elementType;
        }

        /// <summary>
        /// Gets the element type.
        /// </summary>
        public TypeSpec ElementType { get; }

        /// <inheritdoc/>
        public override string Format() => ToString();

        /// <inheritdoc/>
        public override string ToString() => $"{ElementType}*";
    }

    /// <summary>
    /// Represents a reference (&amp;) type.
    /// </summary>
    public class ReferenceTypeSpec : TypeSpec
    {
        public ReferenceTypeSpec(TypeSpec elementType)
        {
            if (elementType is null)
            {
                throw new ArgumentNullException(nameof(elementType));
            }

            ElementType = elementType;
        }

        /// <summary>
        /// Gets the element type.
        /// </summary>
        public TypeSpec ElementType { get; }

        /// <inheritdoc/>
        public override string Format() => ToString();

        /// <inheritdoc/>
        public override string ToString() => $"{ElementType}&";
    }

    /// <summary>
    /// Represents an array type.
    /// </summary>
    public class ArrayTypeSpec : TypeSpec
    {
        public ArrayTypeSpec(TypeSpec elementType, int dimensions)
        {
            if (elementType is null)
            {
                throw new ArgumentNullException(nameof(elementType));
            }

            if (dimensions <= 0)
            {
                throw new ArgumentOutOfRangeException($"An array cannot have a dimension count of {dimensions}");
            }

            ElementType = elementType;
            Dimensions = dimensions;
        }

        /// <summary>
        /// Gets the number of array dimensions.
        /// </summary>
        public int Dimensions { get; }

        /// <summary>
        /// Gets the element type.
        /// </summary>
        public TypeSpec ElementType { get; }

        /// <inheritdoc/>
        public override string Format() => ToString();

        /// <inheritdoc/>
        public override string ToString() => $"{ElementType}[{new string(',', Dimensions - 1)}]";
    }

    /// <summary>
    /// Represents an constructed generic type.
    /// </summary>
    public class ConstructedGenericTypeSpec : TypeSpec
    {
        public ConstructedGenericTypeSpec(NamedTypeSpec unconstructedType, TypeSpec[] arguments)
        {
            if (unconstructedType is null)
            {
                throw new ArgumentNullException(nameof(unconstructedType));
            }

            if (arguments is null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            if (unconstructedType.Arity != arguments.Length)
            {
                throw new ArgumentException($"Invalid number of arguments {arguments.Length} provided while constructing generic type of arity {unconstructedType.Arity}: {unconstructedType}", nameof(arguments));
            }

            foreach (var arg in arguments)
            {
                if (arg is null)
                {
                    throw new ArgumentNullException(nameof(arguments), "Cannot construct a generic type using a null argument");
                }
            }

            UnconstructedType = unconstructedType;
            Arguments = arguments;
        }

        /// <summary>
        /// Gets the unconstructed type.
        /// </summary>
        public NamedTypeSpec UnconstructedType { get; }

        /// <summary>
        /// Gets the type arguments.
        /// </summary>
        public TypeSpec[] Arguments { get; }

        /// <inheritdoc/>
        public override string Format() => ToString();

        /// <inheritdoc/>
        public override string ToString() => $"{UnconstructedType}[{string.Join(",", Arguments.Select(a => $"[{a}]"))}]";
    }

    /// <summary>
    /// Represents a named type, which may be an unconstructed generic type.
    /// </summary>
    public class NamedTypeSpec : TypeSpec
    {
        public NamedTypeSpec(NamedTypeSpec containingType, string name, int arity)
        {
            ContainingType = containingType;
            Name = name;
            if (containingType is NamedTypeSpec c && c.Arity > arity)
            {
                throw new ArgumentException("A named type cannot have an arity less than that of its containing type", nameof(arity));
            }

            if (arity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arity), "A type cannot have a negative arity");
            }

            Arity = arity;
        }

        /// <summary>
        /// Gets the number of generic parameters which this type requires.
        /// </summary>
        public int Arity { get; }

        /// <summary>
        /// Gets the type name, which includes the namespace if this is not a nested type.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the containing type.
        /// </summary>
        public NamedTypeSpec ContainingType { get; }

        /// <summary>
        /// Gets the namespace-qualified type name, including containing types (for nested types).
        /// </summary>
        /// <returns></returns>
        public string GetNamespaceQualifiedName()
        {
            var builder = new StringBuilder();
            GetQualifiedNameInternal(this, builder);
            return builder.ToString();

            static void GetQualifiedNameInternal(NamedTypeSpec n, StringBuilder b)
            {
                if (n.ContainingType is object)
                {
                    GetQualifiedNameInternal(n.ContainingType, b);
                    _ = b.Append('+');
                }

                _ = b.Append(n.Name);
            }
        }

        /// <inheritdoc/>
        public override string Format() => ToString();

        /// <inheritdoc/>
        public override string ToString() => ContainingType is object ? $"{ContainingType}+{Name}" : Name;
    }

    /// <summary>
    /// Represents an assembly-qualified type.
    /// </summary>
    public class AssemblyQualifiedTypeSpec : TypeSpec
    {
        public AssemblyQualifiedTypeSpec(TypeSpec type, string assembly)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (string.IsNullOrWhiteSpace(assembly))
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            Type = type;
            Assembly = assembly;
        }

        /// <summary>
        /// Gets the assembly specification.
        /// </summary>
        public string Assembly { get; }

        /// <summary>
        /// Gets the qualified type.
        /// </summary>
        public TypeSpec Type { get; }

        /// <inheritdoc/>
        public override string Format() => ToString();

        /// <inheritdoc/>
        public override string ToString() => string.IsNullOrWhiteSpace(Assembly) ? Type.ToString() : $"{Type},{Assembly}";
    }
}