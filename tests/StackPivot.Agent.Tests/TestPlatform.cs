namespace StackPivot.Agent.Tests;

internal static class TestPlatform
{
    public static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new Xunit.SkipException("Unix-only test: requires the Unix sleep process.");
        }
    }

    public static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new Xunit.SkipException("Linux-only test: requires memfd and Linux filesystem semantics.");
        }
    }
}
