using System.Collections.Generic;

namespace Orleans
{
    /// <summary>
    /// format the option and give it a category and a name
    /// </summary>
    public interface IOptionFormatter
    {
        /// <summary>
        /// Gets the name of the options object.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Formats the options object into a collection of strings.
        /// </summary>
        /// <returns>A collection of formatted string-value pairs corresponding the the properties on the options object.</returns>
        IEnumerable<string> Format();
    }

    /// <summary>
    /// Option formatter for a certain option type <typeparamref name="T"/>
    /// </summary>
    /// <typeparam name="T">The options type.</typeparam>
    public interface IOptionFormatter<T> : IOptionFormatter
    {
    }

    /// <summary>
    /// IOptionFormatterResolver resolve specific OptionFormatter for certain named option
    /// </summary>
    /// <typeparam name="T">The options type.</typeparam>
    public interface IOptionFormatterResolver<T>
    {
        /// <summary>
        /// Resolves the options formatter for the specified options type with the specified options name.
        /// </summary>
        /// <param name="name">The options name.</param>
        /// <returns>The options type.</returns>
        IOptionFormatter<T> Resolve(string name);
    }

    /// <summary>
    /// Utility class for option formatting
    /// </summary>
    public static class OptionFormattingUtilities
    {
        /// <summary>
        /// The default format string.
        /// </summary>
        private const string DefaultFormatFormatting = "{0}: {1}";

        /// <summary>
        /// The default format string for options types which are named.
        /// </summary>
        private const string DefaultNamedFormatting = "{0}-{1}";

        /// <summary>
        /// Formats a key-value pair using default format
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="value">
        /// The value.
        /// </param>
        /// <param name="formatting">
        /// The format string.
        /// </param>
        /// <returns>A formatted key-value pair.</returns>
        public static string Format(object key, object value, string formatting = null)
        {
            var valueFormat = formatting ?? DefaultFormatFormatting;
            return string.Format(valueFormat, key, value);
        }

        /// <summary>
        /// Formats the name of an options object.
        /// </summary>
        /// <typeparam name="TOptions">The options type.</typeparam>
        /// <param name="name">The options name.</param>
        /// <param name="formatting">The format string.</param>
        /// <returns>The formatted options object name.</returns>
        public static string Name<TOptions>(string name = null, string formatting = null)
        {
            return name is null && formatting is null ? typeof(TOptions).FullName
                : string.Format(formatting ?? DefaultNamedFormatting, typeof(TOptions).FullName, name);
        }
    }
}
