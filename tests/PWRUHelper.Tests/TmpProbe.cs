using System.IO;
using PWRUHelper.Services;
using Xunit;
using Xunit.Abstractions;

namespace PWRUHelper.Tests;

public class TmpProbe
{
    private readonly ITestOutputHelper _o;
    public TmpProbe(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Probe()
    {
        var g = SlangGlossary.FromJson(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "slang.json")));

        foreach (var line in new[]
        {
            "в апа 5-2 нужен хил и танк, стук",
            "бд сегодня, мбг завтра, тв в пятницу",
            "он копал яму, погода огонь, я в ярости",
            "Привет! Как тебя зовут?",
        })
        {
            _o.WriteLine($"IN  : {line}");
            _o.WriteLine($"EXP : {g.Expand(line)}");
            _o.WriteLine($"KEY : {g.Decode(line)}");
            _o.WriteLine("");
        }
    }
}
