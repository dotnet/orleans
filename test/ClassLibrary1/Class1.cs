using Orleans;

namespace ClassLibrary1;


[Alias("I-My@Grain")]
public interface IMyGrain : IGrainWithStringKey
{

}