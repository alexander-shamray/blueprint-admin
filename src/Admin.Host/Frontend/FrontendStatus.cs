using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client as the Stack screen sees it: the last <c>npm start</c>
/// job, running or exited, and whether the clone has <c>node_modules</c>.
/// Whether port 5173 answers is the <c>client</c> entry of the stack's
/// reachability, not repeated here.
/// </summary>
public sealed record FrontendStatus(JobSummary? Job, bool Installed);

public enum FrontendStartOutcome
{
    Started,
    AlreadyRunning,
    NotInstalled,
}

/// <summary><see cref="Job"/> is the new job, the one already running, or null when dependencies are missing.</summary>
public sealed record FrontendStartResult(FrontendStartOutcome Outcome, Job? Job);
