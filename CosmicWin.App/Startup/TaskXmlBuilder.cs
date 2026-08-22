namespace CosmicWin.App.Startup;

/// <summary>
/// Builds the Task Scheduler XML consumed by <c>schtasks /Create /XML</c> (ES-2): a LogonTrigger
/// with <c>RunLevel HighestAvailable</c> running <paramref name="exePath"/>, the only variable
/// content, XML-escaped.
/// </summary>
public static class TaskXmlBuilder
{
    public static string Build(string exePath)
    {
        var escapedPath = System.Security.SecurityElement.Escape(exePath)!;
        return
$"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <RunLevel>HighestAvailable</RunLevel>
      <LogonType>InteractiveToken</LogonType>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{escapedPath}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }
}
