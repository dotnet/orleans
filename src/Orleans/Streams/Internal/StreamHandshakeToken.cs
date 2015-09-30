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

using System;

namespace Orleans.Streams
{
    [Serializable]
    internal class StreamHandshakeToken : IEquatable<StreamHandshakeToken>
    {
        public StreamSequenceToken Token { get; private set; }
        
        public static StreamHandshakeToken CreateStartToken(StreamSequenceToken token)
        {
            if (token == null) return default(StreamHandshakeToken);
            return new StartToken {Token = token};
        }

        public static StreamHandshakeToken CreateDeliveyToken(StreamSequenceToken token)
        {
            if (token == null) return default(StreamHandshakeToken);
            return new DeliveryToken {Token = token};
        }

        public bool Equals(StreamHandshakeToken other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            if (other.GetType() != GetType()) return false;
            return Equals(Token, other.Token);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((StreamHandshakeToken)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (GetType().GetHashCode() * 397) ^ (Token != null ? Token.GetHashCode() : 0);
            }
        }
    }

    [Serializable]
    internal class StartToken : StreamHandshakeToken { }
    
    [Serializable]
    internal class DeliveryToken : StreamHandshakeToken { }
}
