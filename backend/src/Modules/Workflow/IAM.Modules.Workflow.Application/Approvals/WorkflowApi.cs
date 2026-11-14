using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Application.Approvals;

/// <summary>How the API reports a workflow failure.</summary>
public enum WorkflowFailureKind
{
    None = 0,
    NotFound = 1,
    Forbidden = 2,
    WrongState = 3,
    Conflict = 4,
    Invalid = 5,
    Damaged = 6,
}

/// <summary>
/// Maps workflow exceptions to API outcomes by a fixed table (DL-073d), so the Endpoints layer needn't reference the
/// Domain. The code is the refusal's name and nothing else: never a person, reason or route.
/// </summary>
public static class WorkflowApi
{
    public static WorkflowFailureKind Classify(Exception exception, out string? code)
    {
        code = null;
        switch (exception)
        {
            case ApprovalInstanceNotFoundException:
                return WorkflowFailureKind.NotFound;
            case ApprovalConflictException:
                return WorkflowFailureKind.Conflict;
            case ApprovalInstanceDamagedException:
                code = "InstanceDamaged";
                return WorkflowFailureKind.Damaged;
            case ApprovalRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    ApprovalRefusal.NotEligible or ApprovalRefusal.NotRequester or ApprovalRefusal.InvalidActor
                        or ApprovalRefusal.EscalatorConflict or ApprovalRefusal.IndependentEscalatorConflict
                        or ApprovalRefusal.ProposerCannotApproveEscalation or ApprovalRefusal.EscalatePermissionMissing
                        or ApprovalRefusal.CancelPermissionMissing => WorkflowFailureKind.Forbidden,
                    ApprovalRefusal.StaleVersion => WorkflowFailureKind.Conflict,
                    ApprovalRefusal.InstanceClosed or ApprovalRefusal.InstanceHeld or ApprovalRefusal.StageNotActive
                        or ApprovalRefusal.StageNotEscalatable or ApprovalRefusal.StageNotAwaitingApprovers or ApprovalRefusal.AlreadyDecided
                        or ApprovalRefusal.DuplicateDecision or ApprovalRefusal.NoEscalationProposed or ApprovalRefusal.EscalationProposalChanged
                        or ApprovalRefusal.StagePastDeadline or ApprovalRefusal.AlreadyAuthorized or ApprovalRefusal.EscalationNeedsSecondPerson
                        or ApprovalRefusal.SingleEscalatorStage or ApprovalRefusal.TooManyEscalations or ApprovalRefusal.TimeWentBackwards
                        => WorkflowFailureKind.WrongState,
                    _ => WorkflowFailureKind.Invalid,
                };
            default:
                return WorkflowFailureKind.None;
        }
    }
}
