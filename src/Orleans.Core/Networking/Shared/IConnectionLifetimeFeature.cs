using System.Threading;

namespace Orleans.Networking.Shared
{
    /// <remarks>
    /// Duplicate of https://github.com/aspnet/AspNetCore/blob/master/src/Servers/Connections.Abstractions/src/Features/IConnectionLifetimeFeature.cs
    /// </remarks>
    internal interface IConnectionLifetimeFeature
    {
        CancellationToken ConnectionClosed { get; set; }

        void Abort();
    }
}
