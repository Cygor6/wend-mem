using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Wendmem;

/// <summary>
/// Compact console formatter: {HH:mm:ss} {Category,-16} {Message}
/// Errors append full exception with stack trace on subsequent lines.
/// </summary>
public sealed class WendmemConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "wendmem";

    public WendmemConsoleFormatter() : base(FormatterName) { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        if (logEntry.LogLevel < LogLevel.Information)
            return;

        // Category: last segment only
        var category = logEntry.Category;
        var dot = category.LastIndexOf('.');
        if (dot >= 0)
            category = category[(dot + 1)..];

        // Timestamp + category
        textWriter.Write(DateTime.Now.ToString("HH:mm:ss"));
        textWriter.Write(' ');
        textWriter.Write(category.PadRight(16));

        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (logEntry.LogLevel >= LogLevel.Error)
        {
            // Error line: just the exception summary, not the template message
            if (logEntry.Exception is not null)
            {
                textWriter.WriteLine($"{logEntry.Exception.GetType().Name}: {logEntry.Exception.Message}");
                if (!string.IsNullOrEmpty(logEntry.Exception.StackTrace))
                {
                    foreach (var line in logEntry.Exception.StackTrace.Split('\n'))
                        textWriter.WriteLine($"         {line.TrimStart()}");
                }
            }
            else if (!string.IsNullOrEmpty(message))
            {
                textWriter.WriteLine(message);
            }
            else
            {
                textWriter.WriteLine();
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(message))
                textWriter.WriteLine(message);
            else
                textWriter.WriteLine();
        }
    }
}
