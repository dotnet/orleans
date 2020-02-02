using OneBoxDeployment.Api.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;

namespace OneBoxDeployment.Api.Controllers
{
    /// <summary>
    /// A resource to report CSP violations.
    /// </summary>
    [Route("[controller]")]
    [Consumes("application/csp-report")]
    [ApiController]
    public class CspReportController: ControllerBase
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private ILogger Logger { get; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public CspReportController(ILogger<CspReportController> logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }


        /// <summary>
        /// Persists a CSP violation report.
        /// </summary>
        /// <param name="request">The CSP violation report to persist.</param>
        /// <response code="200">The CSP insertion was successful.</response>
        [HttpPost]
        public IActionResult CspReport(CspReportRequest request)
        {
            //Also, as this is somewhat security sensitive, the response is always a success (ignoring timings).
            try
            {
                //This is the only check made. If bad entries will be logged,
                //they should be eliminated and purged from the repository
                //accordingly.
                if(request?.CspReport != null)
                {
                    //TODO: Save the data.
                }
            }
            catch { }

            return Ok();
        }
    }
}
