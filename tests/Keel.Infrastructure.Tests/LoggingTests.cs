using Keel.Infrastructure.Files;
using Keel.Infrastructure.Logging;

namespace Keel.Infrastructure.Tests;

public class LoggingTests
{
    [Fact]
    public void Writes_a_rolling_log_file_in_the_data_directory()
    {
        using var temp = new TempDirectory();
        var dir = new DataDirectory(temp.Path);

        using (var logger = KeelLogging.CreateLogger(dir))
        {
            logger.Information("Started");
        }

        var files = Directory.GetFiles(dir.LogsDirectory, "keel-*.log");
        files.Length.ShouldBe(1);
        File.ReadAllText(files[0]).ShouldContain("Started");
    }
}
