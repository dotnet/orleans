using Orleans;

namespace sandbox;

[Alias("IMyGrain")]
public interface IMyGrain : IGrainWithStringKey
{

}

[Alias("MyGrain")]
public class MyGrain : Grain
{

}
