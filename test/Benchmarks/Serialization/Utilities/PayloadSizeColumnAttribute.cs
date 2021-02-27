using BenchmarkDotNet.Configs;
using System;

namespace Benchmarks.Utilities
{
    [AttributeUsage(AttributeTargets.Class)]
    public class PayloadSizeColumnAttribute : Attribute, IConfigSource
    {
        public PayloadSizeColumnAttribute(string columnName = "Payload")
        {
            var config = ManualConfig.CreateEmpty();
            config.AddColumn(
                new MethodResultColumn(columnName,
                    val =>
                    {
                        uint result;
                        switch (val)
                        {
                            case int i:
                                result = (uint)i;
                                break;
                            case uint i:
                                result = i;
                                break;
                            case long i:
                                result = (uint)i;
                                break;
                            case ulong i:
                                result = (uint)i;
                                break;
                            default: return "Invalid";
                        }

                        return result + " B";
                    }));
            Config = config;
        }

        public IConfig Config { get; }
    }
}