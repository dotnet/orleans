namespace Orleans.AdoNet.Storage;

internal interface ICommandInterceptor
{
    void Intercept(IDbCommand command);
}
