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

namespace Orleans.Streams.Internal
{
    [Serializable]
    internal class StreamDeliveryStartSequenceToken : StreamSequenceToken
    {
        public StreamSequenceToken Token { get; private set; }

        public StreamDeliveryStartSequenceToken(StreamSequenceToken token)
        {
            Token = token;
        }

        public override bool Equals(StreamSequenceToken other)
        {
            var token = other as StreamDeliveryStartSequenceToken;
            if (token == null)
                return false;

            if (Token == null)
                return token.Token == null;

            return Token.Equals(token.Token);
        }

        public override int CompareTo(StreamSequenceToken other)
        {
            if (other == null)
                return 1;

            var token = other as StreamDeliveryStartSequenceToken;
            if (token == null)
                throw new ArgumentOutOfRangeException("other");

            if (Token == null)
                return token.Token == null ? 0: -1;

            return Token.CompareTo(token.Token);
        }

        public static StreamSequenceToken GetToken(StreamSequenceToken token)
        {
            var deliveryStart = token as StreamDeliveryStartSequenceToken;
            return (deliveryStart != null) ? deliveryStart.Token : token;
        }
    }
}
