using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
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

    public class GrainServiceOptionsFormatter : IOptionFormatter<GrainServiceOptions>
    {
        public string Category { get; }

        public string Name => nameof(GrainServiceOptions);
        private GrainServiceOptions options;
        public GrainServiceOptionsFormatter(IOptions<GrainServiceOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return this.options.GrainServices.Select(kvp => OptionFormattingUtilities.Format($"{nameof(this.options.GrainServices)}.{kvp.Key}", kvp.Value)).ToList();
        }
    }
}
