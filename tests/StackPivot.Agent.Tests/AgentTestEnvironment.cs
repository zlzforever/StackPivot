using StackPivot.Agent;

namespace StackPivot.Agent.Tests;

internal static class AgentTestEnvironment
{
    private static readonly object EnvironmentLock = new();

    public static void WithRuntimeCredentialPath(string? path, Action action)
    {
        lock (EnvironmentLock)
        {
            var previous = Environment.GetEnvironmentVariable(AgentOptions.ApiKeyFileEnvironmentVariable);
            Environment.SetEnvironmentVariable(AgentOptions.ApiKeyFileEnvironmentVariable, path);
            try
            {
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentOptions.ApiKeyFileEnvironmentVariable, previous);
            }
        }
    }
}
