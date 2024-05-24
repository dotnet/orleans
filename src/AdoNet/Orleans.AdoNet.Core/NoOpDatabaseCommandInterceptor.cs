using Orleans.AdoNet.Storage;

namespace Orleans.AdoNet.Core;

internal class NoOpCommandInterceptor : ICommandInterceptor
{
    public static readonly ICommandInterceptor Instance = new NoOpCommandInterceptor();

    private NoOpCommandInterceptor()
    {
    }

    public void Intercept(IDbCommand command)
    {
        //NOP
    }
}
