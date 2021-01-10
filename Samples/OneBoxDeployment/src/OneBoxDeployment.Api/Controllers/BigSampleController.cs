using OneBoxDeployment.Api.Dtos;
using OneBoxDeployment.Api.Logging;
using OneBoxDeployment.GrainInterfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orleans;
using System;
using System.Threading.Tasks;

namespace OneBoxDeployment.Api.Controllers
{
    /// <summary>
    /// A sample OneBoxDeployment controller calling Orleans.
    /// </summary>
    [Route("api/[controller]")]
    [Consumes("application/json")]
    [ApiController]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public class OneBoxDeploymentController: ControllerBase
    {
        /// <summary>
        /// The Orleans cluster client.
        /// </summary>
        private IClusterClient ClusterClient { get; }

        /// <summary>
        /// The logger this controller uses.
        /// </summary>
        private ILogger Logger { get; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="logger">The logger the application uses.</param>
        /// <param name="clusterClient">The orleans cluster client.</param>
        public OneBoxDeploymentController(ILogger<OneBoxDeploymentController> logger, IClusterClient clusterClient)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ClusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }


        /// <summary>
        /// Increments grain persistent state by one and returns the result.
        /// </summary>
        /// <param name="increment">The increment information.</param>
        /// <returns>The grain state after increment.</returns>
        [HttpPost(nameof(Increment))]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        public async Task<ActionResult<int>> Increment(Increment increment)
        {
            //This logging is here to demonstrate it can be read in the tests.
            Logger.LogInformation(Events.TestEvent.Id, Events.TestEvent.FormatString, increment);

            var testStateGrain = ClusterClient.GetGrain<ITestStateGrain>(increment.GrainId);
            var currentState = await testStateGrain.Increment(increment.IncrementBy).ConfigureAwait(false);
            return Ok(currentState);
        }
    }
}