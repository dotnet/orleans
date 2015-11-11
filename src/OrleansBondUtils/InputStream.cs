/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

namespace Orleans.Serialization
{
    using System;
    using System.Text;

    using Bond.IO;

    internal sealed class InputStream : IInputStream, ICloneable<InputStream>
    {
        private readonly BinaryTokenStreamReader reader;

        private InputStream(BinaryTokenStreamReader reader)
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

        internal static InputStream Create(BinaryTokenStreamReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }

            return new InputStream(reader);
        }
    }
}
