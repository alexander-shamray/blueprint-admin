using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client as the Stack screen sees it: the last <c>npm start</c>
/// job, running or exited, the last device build beside it, and whether the clone's <c>node_modules</c> is usable:
/// <see cref="FrontendStatus.InstallProblem"/> says why not, and is null while it is (#369).
/// Whether port 5173 answers is the <c>client</c> entry of the stack's
/// reachability, not repeated here.
/// </summary>
public sealed record FrontendStatus(JobSummary? Job, bool Installed, JobSummary? DeviceJob, string? InstallProblem = null);

public enum FrontendStartOutcome
{
    Started,
    AlreadyRunning,
    NotInstalled,
}

/// <summary>
/// <see cref="Job"/> is the new job, the one already running, or null when dependencies are missing or stale, in
/// which case <see cref="Detail"/> is the sentence that says which.
/// </summary>
public sealed record FrontendStartResult(FrontendStartOutcome Outcome, Job? Job, string? Detail = null);
