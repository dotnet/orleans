using System;
using System.Text.RegularExpressions;

namespace Orleans.Streams
{
    /// <summary>
    /// <see cref="IStreamNamespacePredicate"/> implementation allowing to filter stream namespaces by regular
    /// expression.
    /// </summary>
    public class RegexStreamNamespacePredicate : IStreamNamespacePredicate
    {
        internal const string Prefix = "regex:";
        private readonly Regex regex;

        /// <summary>
        /// Returns a pattern used to describe this instance. The pattern will be parsed by an <see cref="IStreamNamespacePredicateProvider"/> instance on each node.
        /// </summary>
        public string PredicatePattern => $"{Prefix}{regex}";

        /// <summary>
        /// Creates an instance of <see cref="RegexStreamNamespacePredicate"/> with the specified regular expression.
        /// </summary>
        /// <param name="regex">The stream namespace regular expression.</param>
        public RegexStreamNamespacePredicate(string regex)
        {
            this.regex = new Regex(regex, RegexOptions.Compiled) ?? throw new ArgumentNullException(nameof(regex));
        }

        /// <inheritdoc />
        public bool IsMatch(string streamNameSpace)
        {
            return regex.IsMatch(streamNameSpace);
        }
    }
}