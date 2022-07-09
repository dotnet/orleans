using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;

namespace Tester.CosmosDB;

public class CosmosDBTestUtils
{
    public static void CheckCosmosDbStorage()
    {
        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountEndpoint)
            || string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountKey))
        {
            throw new SkipException();
        }
    }
}
