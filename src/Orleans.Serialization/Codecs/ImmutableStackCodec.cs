using System.Collections.Generic;
using System.Collections.Immutable;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableStack{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableStackCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableStack<T>, ImmutableStackSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableStackCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableStackCodec(IValueSerializer<ImmutableStackSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableStack<T> ConvertFromSurrogate(ref ImmutableStackSurrogate<T> surrogate) => ImmutableStack.CreateRange(surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableStack<T> value, ref ImmutableStackSurrogate<T> surrogate) => surrogate.Values = new(value);
    }

    /// <summary>
    /// Surrogate type for <see cref="ImmutableStackCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableStackSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public List<T> Values;
    }

    /// <summary>
    /// Copier for <see cref="ImmutableStack{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableStackCopier<T> : IDeepCopier<ImmutableStack<T>>, IOptionalDeepCopier
    {
        private readonly IDeepCopier<T> _copier;

        public ImmutableStackCopier(IDeepCopier<T> copier) => _copier = OrleansGeneratedCodeHelper.GetOptionalCopier(copier);

        public bool IsShallowCopyable() => _copier is null;

        /// <inheritdoc/>
        public ImmutableStack<T> DeepCopy(ImmutableStack<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableStack<T>>(input, out var result))
                return result;

            if (input.IsEmpty || _copier is null)
                return input;

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var items = new List<T>();
            foreach (var item in input)
                items.Add(_copier.DeepCopy(item, context));

            var res = ImmutableStack.CreateRange(items);
            context.RecordCopy(input, res);
            return res;
        }
    }
}
