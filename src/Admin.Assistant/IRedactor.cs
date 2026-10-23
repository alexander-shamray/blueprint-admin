namespace Admin.Assistant;

/// <summary>
/// What every byte the core sends a provider passes first (AI assistant design §10): the caller's text and every tool
/// result. The host supplies it, because only the host knows its secrets; per caller, so a host can redact per caller.
/// </summary>
public interface IRedactor
{
    string Redact(string text, Caller caller);
}
