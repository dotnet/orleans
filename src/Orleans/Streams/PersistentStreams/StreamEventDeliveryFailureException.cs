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
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// This exception indicates that a stream event was not successfully delivered to the consumer.
    /// </summary>
    [Serializable]
    public class StreamEventDeliveryFailureException : OrleansException
    {
        private const string ErrorStringFormat =
            "Stream provider failed to deliver an event.  StreamProvider:{0}  Stream:{1}";

        public StreamEventDeliveryFailureException() { }
        public StreamEventDeliveryFailureException(string message) : base(message) { }
        internal StreamEventDeliveryFailureException(StreamId streamId)
            : base(string.Format(ErrorStringFormat, streamId.ProviderName, streamId)) { }
        public StreamEventDeliveryFailureException(string message, Exception innerException) : base(message, innerException) { }
        public StreamEventDeliveryFailureException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}
