using System;
using Orleans.GrainReferences;

namespace Orleans.Runtime
{
    /// <summary>
    /// Converts <see cref="GrainReference"/> instances to and from <see cref="string"/> values.
    /// </summary>
    public class GrainReferenceKeyStringConverter
    {
        private readonly GrainReferenceActivator _activator;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceKeyStringConverter"/> class.
        /// </summary>
        /// <param name="activator">The grain reference activator.</param>
        public GrainReferenceKeyStringConverter(GrainReferenceActivator activator)
        {
            _activator = activator;
        }        
                
        /// <summary>
        /// Converts the provided grain reference key <see cref="string"/> into a <see cref="GrainReference"/>.
        /// </summary>
        /// <param name="referenceString">
        /// The string representation of a grain reference.
        /// </param>
        /// <returns>The grain reference.</returns>
        public GrainReference FromKeyString(string referenceString)
        {
            var i = referenceString.IndexOf('_');
            if (i < 0) throw new ArgumentException(nameof(referenceString));
            var type = new GrainType(Convert.FromBase64String(referenceString.Substring(0, i)));
            var key = new IdSpan(Convert.FromBase64String(referenceString.Substring(i + 1)));
            var id = new GrainId(type, key);
            return _activator.CreateReference(id, default);
        }

        /// <summary>
        /// Converts the provided <see cref="GrainReference"/> to a string which can be parsed by <see cref="GrainReferenceKeyStringConverter"/>.
        /// </summary>
        /// <param name="grainReference">
        /// The grain reference.
        /// </param>
        /// <returns>The key string.</returns>
        public string ToKeyString(GrainReference grainReference) => grainReference.ToKeyString();
    }

    /// <summary>
    /// Extensions for <see cref="GrainReference"/> for converting to a string which can be parsed by <see cref="GrainReferenceKeyStringConverter"/>.
    /// </summary>
    public static class GrainReferenceConverterExtensions
    {
        /// <summary>
        /// Converts the provided <see cref="GrainReference"/> to a string which can be parsed by <see cref="GrainReferenceKeyStringConverter"/>.
        /// </summary>
        /// <param name="grainReference">
        /// The grain reference.
        /// </param>
        /// <returns>The key string.</returns>
        public static string ToKeyString(this GrainReference grainReference)
        {
            var id = grainReference.GrainId;
            var typeString = Convert.ToBase64String(GrainType.UnsafeGetArray(id.Type));
            var keyString = Convert.ToBase64String(IdSpan.UnsafeGetArray(id.Key));
            return $"{typeString}_{keyString}";
        }
    }
}