using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Orleans
{
    internal sealed class DefaultOptionsFormatter<T> : DefaultOptionsFormatter, IOptionFormatter<T> where T : class, new()
    {
        public DefaultOptionsFormatter(IOptions<T> options) : base(null, options.Value) { }
    }

    internal class DefaultOptionsFormatter : IOptionFormatter<object>
    {
        public string Name { get; }

        private readonly object options;

        internal DefaultOptionsFormatter(string name, object options)
        {
            this.options = options;
            this.Name = OptionFormattingUtilities.Name(options.GetType(), name);
        }

        public IEnumerable<string> Format()
        {
            var result = new List<string>();
            foreach (var prop in options.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (prop.GetGetMethod() is { } && prop.GetSetMethod() is { })
                    FormatProperty(prop, result);
            return result;
        }

        private void FormatProperty(PropertyInfo property, List<string> result)
        {
            var name = property.Name;
            var value = property.GetValue(this.options);
            var redactAttribute = property.GetCustomAttribute<RedactAttribute>(inherit: true);

            // If redact specified, let the attribute implementation do the work
            if (redactAttribute != null)
            {
                result.Add(
                   OptionFormattingUtilities.Format(
                       name,
                       redactAttribute.Redact(value)));
            }
            else
            {
                // If it is a dictionary -> one line per item
                if (value is IDictionary dict)
                {
                    var enumerator = dict.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var kvp = enumerator.Entry;
                        result.Add($"{name}.{kvp.Key}: {kvp.Value}");
                    }
                }
                // If it is a simple collection -> one line per item
                else if (value is ICollection coll)
                {
                    if (coll.Count > 0)
                    {
                        var index = 0;
                        foreach (var item in coll)
                        {
                            result.Add($"{name}.{index}: {item}");
                            index++;
                        }
                    }
                }
                // Simple case
                else
                {
                    result.Add(OptionFormattingUtilities.Format(name, value));
                }
            }
        }
    }

    internal sealed class DefaultOptionsFormatterResolver<T> : IOptionFormatterResolver<T> where T : class
    {
        private readonly IOptionsMonitor<T> optionsMonitor;

        public DefaultOptionsFormatterResolver(IOptionsMonitor<T> optionsMonitor)
        {
            this.optionsMonitor = optionsMonitor;
        }

        public IOptionFormatter<T> Resolve(string name)
        {
            return new DefaultOptionsFormatter(name, this.optionsMonitor.Get(name));
        }
    }
}
