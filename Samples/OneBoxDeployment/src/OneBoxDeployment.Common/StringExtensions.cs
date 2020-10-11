namespace OneBoxDeployment.Common
{
    /// <summary>
    /// String extension methods.
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        /// Ensures the trailing character is appended to the end only once.
        /// </summary>
        /// <param name="str">The string to append the trailing character.</param>
        /// <param name="trailingCharacter">The trailing character.</param>
        /// <returns>The parameter <paramref name="str"/> with the added trailing character.</returns>
        public static string EnsureTrailing(this string str, char trailingCharacter) => str != null ? str.TrimEnd(trailingCharacter) + trailingCharacter : null;
    }
}
