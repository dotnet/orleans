namespace HelloWorld.Interfaces;

// <ihello_interface>
public interface IHello : Orleans.IGrainWithIntegerKey
{
    Task<string> SayHello(string greeting);
}
// </ihello_interface>
