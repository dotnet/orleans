using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Marker object used to denote an unknown field and its position into a stream of data.
    /// </summary>
    public class UnknownFieldMarker
    {
        public UnknownFieldMarker(Field field, long position)
        {
            Field = field;
            Position = position;
        }

        /// <summary>
        /// The position into the stream at which this field occurs.
        /// </summary>
        public long Position { get; }

        /// <summary>
        /// The field header.
        /// </summary>
        public Field Field { get; }

        /// <inheritdoc />
        public override string ToString() => $"{nameof(Position)}: 0x{Position:X}, {nameof(Field)}: {Field}";

    }
}