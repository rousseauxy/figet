using System.Text.Json;
using FiGet.Web;
using Microsoft.Extensions.Logging;

namespace FiGet.Integration.Tests;

[CollectionDefinition(nameof(ConsoleOutput), DisableParallelization = true)]
public sealed class ConsoleOutput;

/// <summary>
/// The JSON log lines a container writes. Each one carries a UTC timestamp: the formatter writes none unless a format is
/// given, and on QA every line came out without a time (2026-11-19).
/// </summary>
[Collection(nameof(ConsoleOutput))]
public sealed class JsonConsoleLogTests
{
    [Fact]
    public void A_json_log_line_carries_a_utc_timestamp()
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            // Disposing the factory flushes the console logger's background queue before the output is read.
            using (var loggers = LoggerFactory.Create(logging => logging.AddJsonConsole(FiGetApp.ConfigureJsonConsole)))
            {
                loggers.CreateLogger("FiGet.Test").LogInformation("timestamp probe {Value}", 42);
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        var line = captured.ToString().Split('\n').Single(l => l.Contains("timestamp probe", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(line);
        var stamp = json.RootElement.GetProperty("Timestamp").GetString();

        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", stamp);
        Assert.Equal("timestamp probe 42", json.RootElement.GetProperty("Message").GetString());
    }
}
