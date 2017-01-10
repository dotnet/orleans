
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.TestingHost.Utils;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    public class HttpApiTests
    {
        [Fact, TestCategory("Functional")]
        public void HttpApiTests_Router()
        {
            var router = new Router();
            var routeHit = false;
            IDictionary<string, string> routeParameters = null;

            // register a route
            router.Add("/grain/:type/:id/:method", (context, parameters) =>
            {
                routeHit = true;
                routeParameters = parameters;
                return TaskDone.Done;
            });

            // test a bad route
            var result1 = router.Match("/foo/bar/baz/qux");
            Assert.False(routeHit);
            Assert.Null(result1);

            // test a good route
            var result2 = router.Match("/grain/IGrain1/Id1/Method1");
            Assert.NotNull(result2);

            // run the route, which will collect the parameters
            result2(null).Wait();

            Assert.True(routeHit);
            Assert.NotNull(routeParameters);
            Assert.Equal("IGrain1", routeParameters["type"]);
            Assert.Equal("Id1", routeParameters["id"]);
            Assert.Equal("Method1", routeParameters["method"]);

        }
    }
}
