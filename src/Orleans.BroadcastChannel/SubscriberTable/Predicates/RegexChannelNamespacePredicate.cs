using System;
using System.Text.RegularExpressions;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// <see cref="IChannelNamespacePredicate"/> implementation allowing to filter stream namespaces by regular
    /// expression.
    /// </summary>
    public class RegexChannelNamespacePredicate : IChannelNamespacePredicate
    {
        internal const string Prefix = "regex:";
        private readonly Regex regex;

        /// <summary>
        /// Returns a pattern used to describe this instance. The pattern will be parsed by an <see cref="IChannelNamespacePredicateProvider"/> instance on each node.
        /// </summary>
        public string PredicatePattern => $"{Prefix}{regex}";

        /// <summary>
        /// Creates an instance of <see cref="RegexChannelNamespacePredicate"/> with the specified regular expression.
        /// </summary>
        /// <param name="regex">The stream namespace regular expression.</param>
        public RegexChannelNamespacePredicate(string regex)
        {
            if (regex is null) throw new ArgumentNullException(nameof(regex));
            
            this.regex = new Regex(regex, RegexOptions.Compiled);
        }

        /// <inheritdoc />
        public bool IsMatch(string streamNameSpace)
        {
            return regex.IsMatch(streamNameSpace);
        }
    }
}