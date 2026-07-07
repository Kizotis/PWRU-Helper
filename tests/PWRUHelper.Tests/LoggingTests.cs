using System.IO;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class LoggingTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PWRUHelperLogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Write_then_ReadRecent_contains_the_message_and_level()
    {
        var dir = TempDir();
        try
        {
            var log = new LogWriter(dir);
            log.Write("INFO", "hello world");

            var recent = log.ReadRecent();
            Assert.Contains("hello world", recent);
            Assert.Contains("[INFO]", recent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rolls_over_and_never_keeps_more_than_two_files()
    {
        var dir = TempDir();
        try
        {
            var log = new LogWriter(dir, maxBytes: 200);   // tiny cap forces frequent rotation
            for (int i = 0; i < 200; i++)
                log.Write("INFO", $"line number {i} with some padding to grow the file");

            var files = Directory.GetFiles(dir);
            Assert.True(files.Length <= 2, $"expected at most 2 log files, found {files.Length}");
            Assert.True(File.Exists(Path.Combine(dir, "log.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "log.1.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadRecent_keeps_the_newest_message_after_rotation()
    {
        var dir = TempDir();
        try
        {
            var log = new LogWriter(dir, maxBytes: 200);
            // Suffix marker ("msg0X") so tokens are unambiguous — "msg0X" is not a substring
            // of "msg10X", and line endings can't make the assertion accidentally pass.
            for (int i = 0; i < 100; i++) log.Write("INFO", $"msg{i}X");

            // The most recent line must always survive; the very first must have rolled off.
            var recent = log.ReadRecent();
            Assert.Contains("msg99X", recent);
            Assert.DoesNotContain("msg0X", recent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadRecent_on_empty_dir_returns_empty_string()
    {
        var dir = TempDir();
        try
        {
            var log = new LogWriter(dir);
            Assert.Equal("", log.ReadRecent());
        }
        finally { Directory.Delete(dir, true); }
    }
}
