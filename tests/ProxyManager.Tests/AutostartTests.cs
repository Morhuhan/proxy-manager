using System.Xml.Linq;
using ProxyManager.Services;

namespace ProxyManager.Tests;

public class AutostartTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Task_xml_starts_current_exe_elevated_without_time_limit()
    {
        var task = XDocument.Parse(Autostart.BuildTaskXml()).Root!;

        var exec = task.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal(Environment.ProcessPath, exec.Element(Ns + "Command")!.Value);
        Assert.Equal(Autostart.Argument, exec.Element(Ns + "Arguments")!.Value);

        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);

        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
    }
}
