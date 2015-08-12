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
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Orleans.Samples.Chirper.GrainInterfaces
{
    /// <summary>
    /// Data object representing one Chirp message entry
    /// </summary>
    [Serializable]
    public class ChirperMessage
    {
        /// <summary>The unique id of this chirp message</summary>
        public Guid MessageId { get; private set; }

        /// <summary>The message content for this chirp message entry</summary>
        public string Message { get; set; }

        /// <summary>The timestamp of when this chirp message entry was originally republished</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>The id of the publisher of this chirp message</summary>
        public long PublisherId { get; set; }

        /// <summary>The user alias of the publisher of this chirp message</summary>
        public string PublisherAlias { get; set; }

        public ChirperMessage()
        {
            this.MessageId = Guid.NewGuid();
        }

        public override string ToString()
        {
            StringBuilder str = new StringBuilder();
            str.Append("Chirp: '").Append(Message).Append("'");
            str.Append(" from @").Append(PublisherAlias);
#if DETAILED_PRINT
            str.Append(" at ").Append(Timestamp.ToString());
#endif       
            return str.ToString();
        }
    }
}
