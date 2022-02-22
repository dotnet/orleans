using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Orleans
{
    /// <summary>
    /// Default implementation of <see cref="IOptionFormatter{T}"/>.
    /// </summary>
    /// <typeparam name="T">The options type.</typeparam>
    internal sealed class DefaultOptionsFormatter<T> : IOptionFormatter<T> where T : class, new()
    {
        private readonly T _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultOptionsFormatter{T}"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        public DefaultOptionsFormatter(IOptions<T> options)
        {
            _options = options.Value;
            Name = OptionFormattingUtilities.Name<T>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultOptionsFormatter{T}"/> class.
        /// </summary>
        /// <param name="name">The options name.</param>
        /// <param name="options">The options.</param>
        internal DefaultOptionsFormatter(string name, T options)
        {
            _options = options;
            Name = OptionFormattingUtilities.Name<T>(name);
        }

        /// <summary>
        /// Gets the options name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// For
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> Format()
        {
            foreach (var prop in typeof(T).GetProperties())
            {
                if (!IsFormattableProperty(prop))
                {
                    continue;
                }

                foreach (var formattedValue in FormatProperty(_options, prop))
                {
                    yield return formattedValue;
                }
            }
        }

        private static bool IsFormattableProperty(PropertyInfo prop)
        {
            if (prop is null) return false;
            if (!IsFormattableType(prop.PropertyType)) return false;
            if (prop.GetCustomAttribute<ObsoleteAttribute>() is not null) return false;
            if (!IsAccessibleMethod(prop.GetSetMethod())) return false;
            if (!IsAccessibleMethod(prop.GetGetMethod())) return false;
            return true;
        }

        private static bool IsAccessibleMethod(MethodInfo accessor)
        {
            if (accessor is null) return false;
            if (!accessor.IsPublic) return false;
            if (accessor.GetCustomAttribute<ObsoleteAttribute>() is not null) return false;
            return true;
        }

        private static bool IsFormattableType(Type type)
        {
            if (type is null) return false;
            if (typeof(Delegate).IsAssignableFrom(type)) return false;
            return true;
        }

        private static IEnumerable<string> FormatProperty(object options, PropertyInfo property)
        {
            var name = property.Name;
            var value = property.GetValue(options);
            var redactAttribute = property.GetCustomAttribute<RedactAttribute>(inherit: true);

            // If redact specified, let the attribute implementation do the work
            if (redactAttribute != null)
            {
                yield return OptionFormattingUtilities.Format(name, redactAttribute.Redact(value));
            }
            else
            {
                if (value is IDictionary dict)
                {
                    // If it is a dictionary -> one line per item
                    var enumerator = dict.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var kvp = enumerator.Entry;
                        yield return $"{name}.{kvp.Key}: {kvp.Value}";
                    }
                }
                else if (value is ICollection coll)
                {
                    // If it is a simple collection -> one line per item
                    if (coll.Count > 0)
                    {
                        var index = 0;
                        foreach (var item in coll)
                        {
                            yield return $"{name}.{index}: {item}";
                            index++;
                        }
                    }
                }
                else
                {
                    // Simple case
                    yield return OptionFormattingUtilities.Format(name, value);
                }
            }
        }
    }

    internal class DefaultOptionsFormatterResolver<T> : IOptionFormatterResolver<T> where T: class, new()
    {
        private readonly IOptionsMonitor<T> _optionsMonitor;

        public DefaultOptionsFormatterResolver(IOptionsMonitor<T> optionsMonitor)
        {
            _optionsMonitor = optionsMonitor;
        }

        public IOptionFormatter<T> Resolve(string name) => new DefaultOptionsFormatter<T>(name, _optionsMonitor.Get(name));
    }
}
