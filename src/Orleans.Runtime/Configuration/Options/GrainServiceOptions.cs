using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration of silo grain services.
    /// </summary>
    public class GrainServiceOptions
    {
        /// <summary>
        /// List of grain services to initialize at startup.  List of full type name (string) and service id (short).
        /// </summary>
        public List<KeyValuePair<string, short>> GrainServices { get; set; } = new List<KeyValuePair<string, short>>();
    }
}
