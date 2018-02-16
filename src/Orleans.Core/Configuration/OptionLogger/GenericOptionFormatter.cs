using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
                .GetProperties(BindingFlags.Public)
                .Where(prop => prop.GetGetMethod() != null && prop.GetSetMethod() != null)
                .Select(FormatProperty)
                .ToList();
        }

        private string FormatProperty(PropertyInfo property)
        {
            var name = property.Name;
            var value = property.GetValue(this.options);
            var redactAttribute = property.GetCustomAttribute<RedactAttribute>(inherit: true);

            return OptionFormattingUtilities.Format(
                name, 
                redactAttribute != null 
                    ? redactAttribute.Redact((string) value)
                    : value);
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
