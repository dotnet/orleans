namespace Orleans.Dashboard.Core;

[Alias("Orleans.Dashboard.Core.ISiloGrainProxy")]
internal interface ISiloGrainProxy : IGrainWithStringKey, ISiloGrainService
{
}
