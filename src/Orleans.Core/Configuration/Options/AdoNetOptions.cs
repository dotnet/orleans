
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// AdoNet settings
    /// </summary>
    public class AdoNetOptions
    {
        /// <summary>
        /// When using ADO, identifies the underlying data provider for liveness and reminders. This three-part naming syntax is also used 
        /// when creating a new factory and for identifying the provider in an application configuration file so that the provider name, 
        /// along with its associated connection string, can be retrieved at run time. https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx
        /// In order to override this value for reminders set <see cref="AdoInvariantForReminders"/> 
        /// </summary>
        public string Invariant { get; set; }

        /// <summary>
        /// Set this property to override <see cref="AdoInvariant"/> for reminders.
        /// </summary>
        public string InvariantForReminders { get; set; }
    }

    public class AdoNetOptionsFormatter : IOptionFormatter<AdoNetOptions>
    {
        public string Category { get; }

        public string Name => nameof(AdoNetOptions);
        private AdoNetOptions options;
        public AdoNetOptionsFormatter(IOptions<AdoNetOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.Invariant), this.options.Invariant),
                OptionFormattingUtilities.Format(nameof(this.options.InvariantForReminders), this.options.InvariantForReminders),
            };
        }
    }
}
