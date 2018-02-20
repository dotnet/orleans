using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Orleans
{
    internal class DefaultOptionsFormatter<T> : IOptionFormatter<T>
         where T : class, new()
    {
        public string Name { get; }

        private T options;

        public DefaultOptionsFormatter(IOptions<T> options)
        {
            this.options = options.Value;
            this.Name = OptionFormattingUtilities.Name<T>();
        }

        internal DefaultOptionsFormatter(string name, T options)
        {
            this.options = options;
            this.Name = OptionFormattingUtilities.Name<T>(name);
        }

        public IEnumerable<string> Format()
        {
            return typeof(T)
                .GetProperties()
                .Where(prop => prop.GetGetMethod() != null && prop.GetSetMethod() != null)
                .SelectMany(FormatProperty)
                .ToList();
        }

        private IEnumerable<string> FormatProperty(PropertyInfo property)
        {
            var result = new List<string>();
            var name = property.Name;
            var value = property.GetValue(this.options);
            var redactAttribute = property.GetCustomAttribute<RedactAttribute>(inherit: true);

            // If it is a dictionary -> one line per item
            if (typeof(IDictionary).IsInstanceOfType(value))
            {
                var dict = (IDictionary)value;
                foreach (DictionaryEntry kvp in dict)
                {
                    result.Add(OptionFormattingUtilities.Format($"{nameof(dict)}.{kvp.Key}", RedactIfNeeded(redactAttribute, kvp.Value)));
                }
            }
            // if it is a simple collection, flatten everything in one line
            else if (typeof(ICollection).IsInstanceOfType(value))
            {
                var coll = (ICollection)value;
                if (coll.Count > 0)
                {
                    StringBuilder sb = null;
                    foreach (var item in coll)
                    {
                        if (sb == null)
                            sb = new StringBuilder(RedactIfNeeded(redactAttribute, item).ToString());
                        else
                            sb.Append($", {RedactIfNeeded(redactAttribute, item).ToString()}");
                    }
                    result.Add(OptionFormattingUtilities.Format(name, sb.ToString()));
                }
            }
            // Simple case, redact if needed
            else
            {
                result.Add(
                    OptionFormattingUtilities.Format(
                        name,
                        RedactIfNeeded(redactAttribute, value)));
            }

            return result;
        }

        private static object RedactIfNeeded(RedactAttribute attribute, object value)
        {
            return attribute != null
                ? attribute.Redact(value)
                : value;
        }
    }

    internal class DefaultOptionsFormatterResolver<T> : IOptionFormatterResolver<T> 
        where T: class, new()
    {
        private IOptionsSnapshot<T> optionsSnapshot;

        public DefaultOptionsFormatterResolver(IOptionsSnapshot<T> optionsSnapshot)
        {
            this.optionsSnapshot = optionsSnapshot;
        }

        public IOptionFormatter<T> Resolve(string name)
        {
            return new DefaultOptionsFormatter<T>(name, optionsSnapshot.Get(name));
        }
    }
}
