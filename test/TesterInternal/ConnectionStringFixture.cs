using Xunit;

namespace UnitTests
{
    public class ConnectionStringFixture
    {
        private Lazy<Task<string>> connectionStringLazy;

        public void InitializeConnectionStringAccessor(Func<Task<string>> connectionStringAccessor)
        {
            Interlocked.CompareExchange(ref connectionStringLazy,
                new Lazy<Task<string>>(connectionStringAccessor, LazyThreadSafetyMode.ExecutionAndPublication), null);
        }

        public string ConnectionString
        {
            get
            {
                if (connectionStringLazy == null)
                {
                    throw new InvalidOperationException(
                        $"{nameof(InitializeConnectionStringAccessor)} was not called before accessing the connection string");
                }

                var connString = connectionStringLazy.Value.Result;
                if (connString != null)
                {
                    return connString;
                }

                throw new SkipException("Environment is not correctly set up to run these tests. Connection string is empty.");
            }
        }
    }
}