namespace System
{
    /// <summary>
    /// Helper extensions for handling strings.
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        /// If the length of the string is greater than the given length, then returns a truncated string to the given length.
        /// Otherwise returns the original string.
        /// </summary>
        /// <param name="str">The string to truncate.</param>
        /// <param name="maxLength">The length upon which to truncate the string.</param>
        public static string Truncate(this string str, int maxLength)
        {
            if (str == null) throw new ArgumentNullException(nameof(str));
            if (maxLength < 0) throw new ArgumentOutOfRangeException(nameof(maxLength));

            return str.Length > maxLength ? str.Substring(0, maxLength) : str;
        }

        /// <summary>
        /// Short-hand for the regular String.IsNullOrEmpty().
        /// </summary>
        /// <param name="str">The string to test.</param>
        public static bool IsNullOrEmpty(this string str)
        {
            return string.IsNullOrEmpty(str);
        }

        /// <summary>
        /// Short-hand for the regular String.IsNullOrWhiteSpace().
        /// </summary>
        /// <param name="str">The string to test.</param>
        public static bool IsNullOrWhiteSpace(this string str)
        {
            return string.IsNullOrWhiteSpace(str);
        }

        /// <summary>
        /// Returns true if the given string is surrounded by at least one space on either side.
        /// </summary>
        /// <param name="str">The string to test.</param>
        public static bool IsUntrimmed(this string str)
        {
            return
                str.Length == 0 ||
                str.Substring(0, 1).Trim().Length == 0 ||
                str.Substring(str.Length - 1, 1).Trim().Length == 0;
        }

        /// <summary>
        /// Returns true if the given string is null or white space or surrounded by at least one space on either side.
        /// </summary>
        /// <param name="str">The string to test.</param>
        public static bool IsNullOrWhiteSpaceOrUntrimmed(this string str)
        {
            return str.IsNullOrWhiteSpace() || str.IsUntrimmed();
        }
    }
}
