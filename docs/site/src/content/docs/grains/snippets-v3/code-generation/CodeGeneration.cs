using System.Reflection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace CodeGeneration;

// Dummy interface for examples
public interface IRuntimeCodeGenGrain : IGrainWithIntegerKey
{
}

public class CodeGenerationExamples
{
    // <with_code_generation>
    public static void ConfigureWithCodeGeneration(ISiloHostBuilder builder)
    {
        builder.ConfigureApplicationParts(
            parts => parts
                .AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly)
                .WithCodeGeneration());
    }
    // </with_code_generation>

    // <with_code_generation_logging>
    public static void ConfigureWithCodeGenerationLogging(ISiloHostBuilder builder)
    {
        ILoggerFactory codeGenLoggerFactory = new LoggerFactory();
        codeGenLoggerFactory.AddProvider(new ConsoleLoggerProvider());
        builder.ConfigureApplicationParts(
            parts => parts
                .AddApplicationPart(typeof(IRuntimeCodeGenGrain).Assembly)
                .WithCodeGeneration(codeGenLoggerFactory));
    }
    // </with_code_generation_logging>

    // <add_application_part>
    public static void ConfigureWithExternalAssembly(ISiloHostBuilder builder)
    {
        var assembly = Assembly.Load("CodeGenAssembly");
        builder.ConfigureApplicationParts(
            parts => parts.AddApplicationPart(assembly));
    }
    // </add_application_part>
}

// Custom console logger provider for the example
public class ConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
    public void Dispose() { }
}

#pragma warning disable CS8633, CS8766
public class ConsoleLogger : ILogger
{
    private readonly string _categoryName;
    public ConsoleLogger(string categoryName) => _categoryName = categoryName;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Console.WriteLine($"[{logLevel}] {_categoryName}: {formatter(state, exception)}");
    }
}
#pragma warning restore CS8633, CS8766
