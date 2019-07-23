namespace Orleans.Runtime.Messaging
{
    internal interface ISiloConnection
    {
        SiloAddress RemoteSiloAddress { get; }
    }
}
