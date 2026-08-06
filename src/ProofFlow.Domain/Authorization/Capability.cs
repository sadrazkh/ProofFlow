namespace ProofFlow.Domain.Authorization;

/// <summary>
/// One permission, checked once, in the back end.
///
/// The brief lists these as separate permissions rather than "what an Admin can do", and they stay
/// separate here: a UI that hides a button is a courtesy, not a control, so every capability is
/// evaluated server-side in an authorization policy before the action runs.
/// </summary>
public enum Capability
{
    // ---- reading -------------------------------------------------------------------------
    ViewProject = 1,
    ViewRun = 2,
    ViewAudit = 3,

    /// <summary>
    /// Reveal a secret's plaintext. Held by almost nobody, and audited every time it is used —
    /// the value leaves the encrypted column only for a caller who has this.
    /// </summary>
    ViewSecret = 4,

    // ---- authoring -----------------------------------------------------------------------
    CreateTest = 10,
    EditTest = 11,
    DeleteTest = 12,
    ManageDataSet = 13,

    // ---- execution -----------------------------------------------------------------------
    RunTest = 20,
    CancelRun = 21,
    DeleteRun = 22,

    // ---- baselines -----------------------------------------------------------------------
    /// <summary>Capture a response and propose it as a baseline. Proposing is not approving.</summary>
    RecordBaseline = 30,

    /// <summary>Move a baseline to Approved. Separated from RecordBaseline so the two can be
    /// held by different people, which is the entire point of the review workflow.</summary>
    ApproveBaseline = 31,

    // ---- configuration -------------------------------------------------------------------
    ManageEnvironment = 40,
    ManageSecret = 41,
    ManageSchedule = 42,
    ManageProject = 43,
    ManageMembers = 44,
    ManageRunner = 45,
    ExportProject = 46,
    ImportProject = 47,
}
