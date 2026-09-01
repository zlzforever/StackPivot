namespace StackPivot.Agent;

public sealed record AgentOptions(
    Guid AgentId,
    string ControlHubUrl,
    string ApiKey,
    string AgentRoot);
