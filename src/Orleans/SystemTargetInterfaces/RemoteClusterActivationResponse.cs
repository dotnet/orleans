﻿using System;
using System.Text;
using Orleans.GrainDirectory;

namespace Orleans.SystemTargetInterfaces
{
    /// <summary>
    /// Reponse message used by Global Single Instance Protocol
    /// </summary>
    [Serializable]
    internal class RemoteClusterActivationResponse
    {
        public static readonly RemoteClusterActivationResponse Pass = new RemoteClusterActivationResponse(ActivationResponseStatus.Pass);

        public RemoteClusterActivationResponse(ActivationResponseStatus responseStatus)
        {
            this.ResponseStatus = responseStatus;
        }
        public ActivationResponseStatus ResponseStatus { get; private set; }
        public AddressAndTag ExistingActivationAddress { get; set; }
        public string ClusterId { get; set; }
        public bool Owned { get; set; }
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