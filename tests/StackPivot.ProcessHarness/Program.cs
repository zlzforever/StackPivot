using System.Diagnostics;
using System.Globalization;

if (args.Length == 1 && string.Equals(args[0], "emit-and-hold", StringComparison.Ordinal))
{
    Console.WriteLine("child-line");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args.Length != 2
    || args[0] is not ("spawn-child" or "spawn-child-and-exit"))
{
    return 2;
}

var inheritsOutput = string.Equals(args[0], "spawn-child-and-exit", StringComparison.Ordinal);
var childStartInfo = new ProcessStartInfo
{
    FileName = inheritsOutput ? Environment.ProcessPath! : "sleep",
    UseShellExecute = false,
    RedirectStandardOutput = !inheritsOutput,
    RedirectStandardError = !inheritsOutput,
    CreateNoWindow = true
};
if (inheritsOutput)
{
    childStartInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
    childStartInfo.ArgumentList.Add("emit-and-hold");
}
else
{
    childStartInfo.ArgumentList.Add("30");
}
using var child = Process.Start(childStartInfo)
    ?? throw new InvalidOperationException("Unable to start the child process.");
File.WriteAllText(args[1], child.Id.ToString(CultureInfo.InvariantCulture));
if (inheritsOutput)
{
    Console.WriteLine("parent-line");
    return 0;
}

await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
