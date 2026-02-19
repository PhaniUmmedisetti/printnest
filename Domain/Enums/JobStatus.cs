namespace PrintNest.Domain.Enums;

/// <summary>
/// All possible states a PrintJob can be in.
///
/// Flow (happy path):
///   Draft → Uploaded → Quoted → Paid → Released → Downloading → Printing → Completed → Deleted
///
/// Terminal error states:
///   Expired  — job was not released before the 7-day job lifetime or OTP was not used in 6h
///   Failed   — device reported a print failure
///
/// Deleted is the final state for all paths.
///
/// To add a new state: add it here, then add the transition(s) in JobStateMachine.cs.
/// </summary>
public enum JobStatus
{
    /// <summary>Job created, presigned upload URL issued. File not yet in storage.</summary>
    Draft,

    /// <summary>File confirmed in storage. Ready to be quoted.</summary>
    Uploaded,

    /// <summary>Price calculated and stored. Awaiting payment.</summary>
    Quoted,

    /// <summary>Payment confirmed (mock in MVP). OTP can be generated.</summary>
    Paid,

    /// <summary>OTP validated at kiosk. Job bound to a specific device. File token issued.</summary>
    Released,

    /// <summary>Device has started downloading the file via the file token.</summary>
    Downloading,

    /// <summary>Device has received the file and sent it to the printer (CUPS job submitted).</summary>
    Printing,

    /// <summary>Printer confirmed job completed successfully. File deletion pending.</summary>
    Completed,

    /// <summary>File deleted from storage and all local device copies removed.</summary>
    Deleted,

    /// <summary>
    /// Job expired before being released or completed.
    /// Set by the background expiry worker — never by user or device actions.
    /// </summary>
    Expired,

    /// <summary>
    /// Print failed on the device (printer error, paper jam, etc.).
    /// Set by the device calling the /failed endpoint.
    /// </summary>
    Failed
}
