using System;
using System.Text.RegularExpressions;

namespace Orleans.Streams
{
    [Serializable]
    public class RegexStreamNamespacePredicate : IStreamNamespacePredicate
    {
        private readonly Regex regex;

        public RegexStreamNamespacePredicate(Regex regex)
        {
            this.regex = regex;
        }

        public bool IsMatch(string streamNameSpace)
        {
            return regex.IsMatch(streamNameSpace);
        }
    }
}