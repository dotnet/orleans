using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for ADO.NET reminder storage.
    /// </summary>
    public class AdoNetReminderTableOptions
    {
        /// <summary>
        /// Gets or sets the ADO.NET invariant.
        /// </summary>
        public string Invariant { get; set; }

        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        public string ConnectionString { get; set; }
    }

    /// <inheritdoc />
    internal class AdoNetReminderTableOptionsFormatter : IOptionFormatter<AdoNetReminderTableOptions>
    {
        private readonly AdoNetReminderTableOptions options;

        public AdoNetReminderTableOptionsFormatter(IOptions<AdoNetReminderTableOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public string Name => nameof(AdoNetReminderTableOptions);

        /// <inheritdoc />
        public IEnumerable<string> Format()
        {
            return new[]
            {
                OptionFormattingUtilities.Format(nameof(this.options.Invariant), this.options.Invariant),
                OptionFormattingUtilities.Format(nameof(this.options.ConnectionString), ConfigUtilities.RedactConnectionStringInfo(this.options.ConnectionString))
            };
        }
    }
}