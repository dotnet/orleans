using System.CommandLine;
using System.CommandLine.Parsing;

namespace DistributedTests
{
    public static class OptionHelper
    {
        public static Option<T> CreateOption<T>(string alias, string description = null, bool isRequired = false, T defaultValue = default, Func<T, bool> validator = null)
        {
            var options = new Option<T>(alias, description) { IsRequired = isRequired };
            if (!isRequired)
            {
                options.SetDefaultValue(defaultValue);
            }
            if (validator != null)
            {
                options.AddValidator(result => Validator(result, validator));
            }
            return options;
        }

        public static string Validator<T>(OptionResult result, Func<T, bool> validator)
        {
            var value = result.GetValueOrDefault<T>();
            if (!validator(value))
            {
                return $"Option {result.Token?.Value} cannot be set to {value}";
            }
            return string.Empty;
        }

        public static bool OnlyStrictlyPositive(int value) => value > 0;
        public static bool OnlyPositiveOrZero(int value) => value >= 0;
    }
}
