using System;
using System.Text;
using Orleans.GrainDirectory;

namespace Orleans.SystemTargetInterfaces
{
    internal enum ActivationResponseStatus
    {
        Pass,
        Failed,
        Faulted
    }

    /// <summary>
    /// Response message used by Global Single Instance Protocol
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    internal class RemoteClusterActivationResponse
    {
        public static readonly RemoteClusterActivationResponse Pass = new RemoteClusterActivationResponse(ActivationResponseStatus.Pass);

        public RemoteClusterActivationResponse(ActivationResponseStatus responseStatus)
        {
            this.ResponseStatus = responseStatus;
        }

        [Id(1)]
        public ActivationResponseStatus ResponseStatus { get; private set; }
        [Id(2)]
        public AddressAndTag ExistingActivationAddress { get; set; }
        [Id(3)]
        public string ClusterId { get; set; }
        [Id(4)]
        public bool Owned { get; set; }
        [Id(5)]
        public Exception ResponseException { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[");
            sb.Append(ResponseStatus.ToString());
            if (ExistingActivationAddress.Address != null) {
                sb.Append(" ");
                sb.Append(ExistingActivationAddress.Address);
                sb.Append(" ");
                sb.Append(ClusterId);
            }
            if (Owned)
            {
                sb.Append(" owned");
            }
            if (ResponseException != null)
            {
                sb.Append(" ");
                sb.Append(ResponseException.GetType().Name);
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}
