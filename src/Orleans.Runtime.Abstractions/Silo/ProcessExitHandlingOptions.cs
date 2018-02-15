using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// ProcessExitHandlingOptions configure silo behavior on process exit
    /// </summary>
    public class ProcessExitHandlingOptions
    {
        /// <summary>
        /// Whether to fast kill a silo on process exit or not. Turned on by default 
        /// </summary>
        public bool FastKillOnProcessExit { get; set; } = true;
    }

    public class ProcessExitHandlingOptionsFormatter : IOptionFormatter<ProcessExitHandlingOptions>
    {
        public string Name => nameof(ProcessExitHandlingOptions);

        private ProcessExitHandlingOptions options;
        public ProcessExitHandlingOptionsFormatter(IOptions<ProcessExitHandlingOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.FastKillOnProcessExit),this.options.FastKillOnProcessExit),
            };
        }
    }
}
