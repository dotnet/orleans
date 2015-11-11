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

    internal sealed class OutputStream : IOutputStream
    {
        private readonly BinaryTokenStreamWriter writer;

        private OutputStream(BinaryTokenStreamWriter writer)
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

        internal static OutputStream Create(BinaryTokenStreamWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            return new OutputStream(writer);
        }
    }
}