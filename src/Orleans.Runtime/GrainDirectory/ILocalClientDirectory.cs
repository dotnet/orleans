namespace Orleans.Runtime.GrainDirectory
{
    internal interface ILocalClientDirectory
    {
        ClientRoutingTableSnapshot GetRoutingTable();
    }
}
