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
