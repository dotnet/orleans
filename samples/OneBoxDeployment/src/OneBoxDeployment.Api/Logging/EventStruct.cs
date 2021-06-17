using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace OneBoxDeployment.Api.Logging
{
    /// <summary>
    /// Ties together the event ID and corresponding format string.
    /// </summary>
    [DebuggerDisplay("EventStruct(Id = Id, FormatString = {FormatString})")]
    public readonly struct EventStruct
    {
        /// <summary>
        /// The ID of the logging event. This should be unique per purpose.
        /// </summary>
        public EventId Id { get; }

        /// <summary>
        /// The string used to format the message related to the <see cref="Id"/>.
        /// </summary>
        public string FormatString { get; }


        /// <summary>
        /// Constructs an event together with its formatting string.
        /// </summary>
        /// <param name="id">The ID of the logging event. This should be unique per purpose.</param>
        /// <param name="formatString">The string used to format the message related to the <paramref name="id"/>.</param>
        public EventStruct(EventId id, string formatString)
        {
            Id = id;
            FormatString = formatString ?? throw new ArgumentNullException(nameof(formatString));
        }
    }
}