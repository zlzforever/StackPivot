using StackPivot.Contracts.Deployments;

namespace StackPivot.Control.Domain.Entities;

public sealed class DeploymentRequestEntity
{
    public Guid RequestId { get; set; }
    public Guid StackId { get; set; }
    public Guid UserId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string TargetCommitHash { get; set; } = string.Empty;
    public DeploymentMode Mode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Stack? Stack { get; set; }
    public ICollection<ServiceOperationHistory> Operations { get; } = new List<ServiceOperationHistory>();
}
