using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    using System;
    using System.Text;

    /// <summary>
    /// Represents an exception which cannot be fully deserialized.
    /// </summary>
    [Serializable]
    public class RemoteNonDeserializableException : OrleansException
    {
        public RemoteNonDeserializableException() { }

        /// <summary>
        /// Gets the type name of the original <see cref="Exception"/> represented by this instance.
        /// </summary>
        public string OriginalTypeName { get; internal set; }
        
        /// <summary>
        /// Gets or sets the additional data deserialized alongside this instance, for example, exception subclass fields.
        /// </summary>
        public byte[] AdditionalData { get; internal set; }

        /// <summary>
        /// Returns a <see cref="string"/> representation of this instance.
        /// </summary>
        /// <returns>A <see cref="string"/> representation of this instance.</returns>
        public override string ToString()
        {
            if (string.IsNullOrWhiteSpace(this.OriginalTypeName)) return base.ToString();

            var builder = new StringBuilder();
            builder.Append(this.OriginalTypeName);
            if (!string.IsNullOrWhiteSpace(this.Message))
            {
                builder.Append(": ").Append(this.Message);
            }

            if (this.InnerException != null)
            {
                builder.Append(" ---> ")
                       .AppendLine(this.InnerException.ToString())
                       .Append("   --- End of inner exception stack trace ---");
            }

            builder.AppendLine();
            builder.Append(this.StackTrace);
            return builder.ToString();
        }
        
#if !NETSTANDARD
        public RemoteNonDeserializableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.OriginalTypeName = info.GetString(nameof(this.OriginalTypeName));
            this.AdditionalData = (byte[]) info.GetValue(nameof(this.AdditionalData), typeof(byte[]));
        }

        /// <inheritdoc />
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(this.OriginalTypeName), this.OriginalTypeName);
            info.AddValue(nameof(this.AdditionalData), this.AdditionalData);

            base.GetObjectData(info, context);
        }
#endif
    }
}