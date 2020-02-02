using System;
using System.Text.RegularExpressions;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Serialization;

namespace Orleans.Streams
{
    /// <summary>
    /// <see cref="IStreamNamespacePredicate"/> implementation allowing to filter stream namespaces by regular
    /// expression.
    /// </summary>
    [Serializable]
    [Immutable]
    public class RegexStreamNamespacePredicate : IStreamNamespacePredicate
    {
        private readonly Regex regex;

        /// <summary>
        /// Creates an instance of <see cref="RegexStreamNamespacePredicate"/> with the specified regular expression.
        /// </summary>
        /// <param name="regex">The stream namespace regular expression.</param>
        public RegexStreamNamespacePredicate(Regex regex)
        {
            this.regex = regex ?? throw new ArgumentNullException(nameof(regex));
        }

        /// <inheritdoc />
        public bool IsMatch(string streamNameSpace)
        {
            return regex.IsMatch(streamNameSpace);
        }

        [CopierMethod]
        public static object DeepCopier(object original, ICopyContext context)
        {
            return original;
        }

        [SerializerMethod]
        public static void Serializer(object untypedInput, ISerializationContext context, Type expected)
        {
            var input = (RegexStreamNamespacePredicate)untypedInput;
            var regex = input.regex;
            context.StreamWriter.Write(regex.ToString());
            context.StreamWriter.Write((int)regex.Options);
        }

        [DeserializerMethod]
        public static object Deserializer(Type expected, IDeserializationContext context)
        {
            var pattern = context.StreamReader.ReadString();
            var options = (RegexOptions)context.StreamReader.ReadInt();
            return new RegexStreamNamespacePredicate(new Regex(pattern, options));
        }
    }
}