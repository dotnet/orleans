//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

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
