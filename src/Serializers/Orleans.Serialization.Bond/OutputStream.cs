namespace Orleans.Serialization
{
    using System;
    using System.Text;
    using Bond.IO;

    internal sealed class OutputStream : IOutputStream
    {
        private readonly IBinaryTokenStreamWriter writer;

        private OutputStream(IBinaryTokenStreamWriter writer)
        {
            this.writer = writer;
        }

        public void WriteUInt8(byte value)
        {
            this.writer.Write(value);
        }

        public void WriteUInt16(ushort value)
        {
            this.writer.Write(value);
        }

        public void WriteUInt32(uint value)
        {
            this.writer.Write(value);
        }

        public void WriteUInt64(ulong value)
        {
            this.writer.Write(value);
        }

        public void WriteFloat(float value)
        {
            this.writer.Write(value);
        }

        public void WriteDouble(double value)
        {
            this.writer.Write(value);
        }

        public void WriteBytes(ArraySegment<byte> data)
        {
            this.writer.Write(data.Array, data.Offset, data.Count);
        }

        public void WriteVarUInt16(ushort value)
        {
            this.writer.Write(value);
        }

        public void WriteVarUInt32(uint value)
        {
            this.writer.Write(value);
        }

        public void WriteVarUInt64(ulong value)
        {
            this.writer.Write(value);
        }

        public void WriteString(Encoding encoding, string value, int size)
        {
            var bytes = encoding.GetBytes(value);
            if (bytes.Length != size)
            {
                throw new ArgumentException("the size doesn't correspond to the actual size of the array of encoded bytes", "size");
            }

            this.writer.Write(bytes);
        }

        public long Position
        {
            get
            {
                return this.writer.CurrentOffset;
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        internal static OutputStream Create(IBinaryTokenStreamWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            return new OutputStream(writer);
        }
    }
}
