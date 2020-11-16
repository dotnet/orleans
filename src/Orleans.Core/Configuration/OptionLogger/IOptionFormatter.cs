using System;
using System.Collections.Generic;

namespace Orleans
{
    /// <summary>
    /// format the option and give it a category and a name
    /// </summary>
    public interface IOptionFormatter
    {
        string Name { get; }

        //format setting values into a list of string
        IEnumerable<string> Format();
    }

    /// <summary>
    /// Option formatter for a certain option type <typeparamref name="T"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IOptionFormatter<T> : IOptionFormatter
    {
        
    }

    /// <summary>
    /// IOptionFormatterResolver resolve specific OptionFormatter for certain named option
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IOptionFormatterResolver<T>
    {
        IOptionFormatter<T> Resolve(string name);
    }

    /// <summary>
    /// Utility class for option formatting
    /// </summary>
    public static class OptionFormattingUtilities
    {
        private const string DefaultFormatFormatting = "{0}: {1}";
        private const string DefaultNamedFormatting = "{0}-{1}";

        /// <summary>
        /// Format key value pair usin default format
        /// </summary>
        public static string Format(object key, object value, string formatting = null)
        {
            var valueFormat = formatting ?? DefaultFormatFormatting;
            return string.Format(valueFormat, key, value);
        }

        public static string Name<TOptions>(string name = null, string formatting = null)
        {
            return name is null && formatting is null ? typeof(TOptions).FullName
                : string.Format(formatting ?? DefaultNamedFormatting, typeof(TOptions).FullName, name);
        }
    }
}
