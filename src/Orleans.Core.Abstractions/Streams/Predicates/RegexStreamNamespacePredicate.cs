using System;
using System.Text.RegularExpressions;

namespace Orleans.Streams
{
    /// <summary>
    /// <see cref="IStreamNamespacePredicate"/> implementation allowing to filter stream namespaces by regular
    /// expression.
    /// </summary>
    [Serializable]
    public class RegexStreamNamespacePredicate : IStreamNamespacePredicate
    {
        private readonly Regex regex;

        /// <summary>
        /// Creates an instance of <see cref="RegexStreamNamespacePredicate"/> with the specified regular expression.
        /// </summary>
        /// <param name="regex">The stream namespace regular expression.</param>
        public RegexStreamNamespacePredicate(Regex regex)
        {
            this.regex = regex;
        }

        /// <inheritdoc cref="IStreamNamespacePredicate"/>
        public bool IsMatch(string streamNameSpace)
        {
            return regex.IsMatch(streamNameSpace);
        }
    }
}