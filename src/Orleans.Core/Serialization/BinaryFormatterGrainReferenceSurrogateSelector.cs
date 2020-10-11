using System;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Orleans.GrainReferences;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// <see cref="ISurrogateSelector"/> implementation for <see cref="BinaryFormatter"/> for <see cref="GrainReference"/> implementations.
    /// </summary>
    public class BinaryFormatterGrainReferenceSurrogateSelector : ISurrogateSelector
    {
        private readonly BinaryFormatterGrainReferenceSurrogate _surrogate;
        private ISurrogateSelector _chainedSelector;

        public BinaryFormatterGrainReferenceSurrogateSelector(GrainReferenceActivator activator)
        {
            _surrogate = new BinaryFormatterGrainReferenceSurrogate(activator);
        }

        /// <inheritdoc/>
        public void ChainSelector(ISurrogateSelector selector)
        {
            if (_chainedSelector is null)
            {
                _chainedSelector = selector;
            }
            else
            {
                _chainedSelector.ChainSelector(selector);
            }
        }

        /// <inheritdoc/>
        public ISurrogateSelector GetNextSelector() => _chainedSelector;

        /// <inheritdoc/>
        public ISerializationSurrogate GetSurrogate(Type type, StreamingContext context, out ISurrogateSelector selector)
        {
            if (typeof(GrainReference).IsAssignableFrom(type))
            {
                selector = this;
                return _surrogate;
            }

            if (_chainedSelector is object)
            {
                return _chainedSelector.GetSurrogate(type, context, out selector);
            }

            selector = null;
            return null;
        }
    }

    /// <summary>
    /// Serialization surrogate to be used with <see cref="BinaryFormatterGrainReferenceSurrogateSelector"/>.
    /// </summary>
    public class BinaryFormatterGrainReferenceSurrogate : ISerializationSurrogate
    {
        private readonly GrainReferenceActivator _activator;
        public BinaryFormatterGrainReferenceSurrogate(GrainReferenceActivator activator)
        {
            _activator = activator;
        }

        /// <inheritdoc/>
        public void GetObjectData(object obj, SerializationInfo info, StreamingContext context)
        {
            var typed = (GrainReference)obj;
            info.AddValue("type", typed.GrainId.Type.ToStringUtf8(), typeof(string));
            info.AddValue("key", typed.GrainId.Key.ToStringUtf8(), typeof(string));
            info.AddValue("interface", typed.InterfaceType.ToStringUtf8(), typeof(string));
        }

        /// <inheritdoc/>
        public object SetObjectData(object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector)
        {
            var id = GrainId.Create(info.GetString("type"), info.GetString("key"));
            var iface = GrainInterfaceType.Create(info.GetString("interface"));
            return _activator.CreateReference(id, iface);
        }
    }
}
