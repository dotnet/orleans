using System;
using System.Linq;
using System.Text;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    public ref struct StreamIdentityToken
    {
        public StreamIdentityToken(in Guid streamGuid, string streamNamespace)
        {
            this.Guid = streamGuid;
            this.Namespace = streamNamespace;
            this.Token = Create(streamGuid, streamNamespace);
        }

        public StreamIdentityToken(byte[] streamIdentityToken)
        {
            Parse(streamIdentityToken, out Guid streamGuid, out string streamNamespace);
            this.Guid = streamGuid;
            this.Namespace = streamNamespace;
            this.Token = streamIdentityToken;
        }

        public Guid Guid { get; }
        public string Namespace { get; }
        public byte[] Token { get; }

        public static byte[]  Create(in Guid streamGuid, string streamNamespace)
        {
            byte[] guidBytes = streamGuid.ToByteArray();
            byte[] streamNamespaceByte = (streamNamespace is null)
                ? null
                : Encoding.ASCII.GetBytes(streamNamespace);
            int size = guidBytes.Length + // of guid
                (streamNamespaceByte == null // sizeof namespace
                ? 0
                : streamNamespaceByte.Length);
            byte[] token = new byte[size];
            Buffer.BlockCopy(guidBytes, 0, token, 0, guidBytes.Length);
            if (streamNamespaceByte != null)
            {
                Buffer.BlockCopy(streamNamespaceByte, 0, token, guidBytes.Length, streamNamespaceByte.Length);
            }
            return token;
        }

        public static byte[] Create(IStreamIdentity streamIdentity) => Create(streamIdentity.Guid, streamIdentity.Namespace);

        private static void Parse(byte[] token, out Guid streamGuid, out string streamNamespace)
        {
            streamGuid = new Guid(new ArraySegment<byte>(token, 0, 16).ToArray());
            streamNamespace = token.Length > 16
                ? Encoding.ASCII.GetString(new ArraySegment<byte>(token, 16, token.Length - 16).ToArray())
                : default;
        }
    }
}
