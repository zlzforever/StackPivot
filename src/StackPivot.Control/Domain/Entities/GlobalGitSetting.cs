namespace StackPivot.Control.Domain.Entities;

public sealed class GlobalGitSetting
{
    public int Id { get; set; }
    public string GitRepo { get; set; } = string.Empty;
    public string GitUserName { get; set; } = string.Empty;
    public string AccessTokenEncrypted { get; set; } = string.Empty;
    public string TokenKeyId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
