namespace Orleans.Serialization
{
    using System;
    using System.Text;
    using Bond.IO;

    internal sealed class InputStream : IInputStream, ICloneable<InputStream>
    {
        private readonly IBinaryTokenStreamReader reader;

        private InputStream(IBinaryTokenStreamReader reader)
        {
            this.reader = reader;
        }

        public byte ReadUInt8()
        {
            return this.reader.ReadByte();
        }

        public ushort ReadUInt16()
        {
            return this.reader.ReadUShort();
        }

        public uint ReadUInt32()
        {
            return this.reader.ReadUInt();
        }

        public ulong ReadUInt64()
        {
            return this.reader.ReadULong();
        }

        public float ReadFloat()
        {
            return this.reader.ReadFloat();
        }

        public double ReadDouble()
        {
            return this.reader.ReadDouble();
        }

        public ArraySegment<byte> ReadBytes(int count)
        {
            return new ArraySegment<byte>(this.reader.ReadBytes(count));
        }

        public void SkipBytes(int count)
        {
            this.reader.ReadBytes(count);
        }

        public ushort ReadVarUInt16()
        {
            return this.reader.ReadUShort();
        }

        public uint ReadVarUInt32()
        {
            return this.reader.ReadUInt();
        }

        public ulong ReadVarUInt64()
        {
            return this.reader.ReadULong();
        }

        public string ReadString(Encoding encoding, int size)
        {
            return encoding.GetString(this.reader.ReadBytes(size));
        }

        public long Length
        {
            get
            {
                return this.reader.CurrentPosition;
            }
        }

        public long Position
        {
            get
            {
                return this.reader.CurrentPosition;
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public InputStream Clone()
        {
            return new InputStream(this.reader.Copy());
        }

        internal static InputStream Create(IBinaryTokenStreamReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            return new InputStream(reader);
        }
    }
}
