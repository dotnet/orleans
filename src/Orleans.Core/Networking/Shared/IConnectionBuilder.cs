using System;

namespace Orleans.Networking.Shared
{
    internal interface IConnectionBuilder
    {
        IServiceProvider ApplicationServices { get; }

        IConnectionBuilder Use(Func<ConnectionDelegate, ConnectionDelegate> middleware);

        ConnectionDelegate Build();
    }
}
