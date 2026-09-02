using System.Diagnostics;
using System.Globalization;

if (args.Length != 2 || !string.Equals(args[0], "spawn-child", StringComparison.Ordinal))
{
    return 2;
}

var childStartInfo = new ProcessStartInfo
{
    FileName = "sleep",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};
childStartInfo.ArgumentList.Add("30");
using var child = Process.Start(childStartInfo)
    ?? throw new InvalidOperationException("Unable to start the child process.");
File.WriteAllText(args[1], child.Id.ToString(CultureInfo.InvariantCulture));
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
