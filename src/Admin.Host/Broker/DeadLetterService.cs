using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;

namespace Admin.Host.Broker;

/// <summary>
/// A parked message read, replayed or discarded with blueprint-backend's <c>tools/dead-letters</c>, as run-locally.md's
/// "Replay or discard a dead letter" does it: give <see cref="Operator"/> a password, run the tool with it in
/// <see cref="PasswordVariable"/>, take the password away. The console reads no queue and moves no message itself;
/// the tool's README owns how, and its runbooks own when.
/// </summary>
/// <remarks>
/// The password is random, minted for one run and cleared when the run ends, whether the tool succeeded or not, so
/// between runs the account has none, as it ships, and nothing holds one (spec §8). Runs are one at a time: a second
/// run's clear would end the first one's login. The clear is attempted from the moment the set is, since a set that
/// timed out may still have landed. The set, the tool and the clear are all listed processes the operator caused, so
/// the transcript holds the whole run, with the password scrubbed wherever it appears (<see cref="ProcessSpec.Secrets"/>).
/// </remarks>
public sealed partial class DeadLetterService(
    ComposeService compose,
    IProcessRunner runner,
    JobRegistry registry,
    RepoPaths paths,
    string recordDir,
    TimeProvider time)
{
    /// <summary>The broker account the tool runs as, declared with no password in the backend's <c>definitions.json</c>.</summary>
    public const string Operator = "dead-letter-operator";

    /// <summary>Where the tool reads the operator's password from (the tool's README, "The credential").</summary>
    public const string PasswordVariable = "DEAD_LETTERS_PASSWORD";

    /// <summary>The tool, from the backend clone, as run-locally.md runs it.</summary>
    public const string Tool = "tools/dead-letters/dead_letters.py";

    /// <summary>What a person types to carry a move out, spelled as the flag that does.</summary>
    public const string ExecuteConfirm = "--execute";

    /// <summary>How many messages one inspection reads and puts back; the tool's own default is 100.</summary>
    public const int InspectLimit = 20;

    /// <summary>The prefix the tool gives the stderr line that is the last copy of a message it could not record.</summary>
    public const string UnrecordedPrefix = "unrecorded ";

    /// <summary>The Python run-locally.md runs the tool with.</summary>
    public static readonly string[] Python = ["py", "-3.12"];

    public static readonly string[] ClearPasswordArguments = ["rabbitmqctl", "clear_password", Operator];

    private readonly Lock gate = new();
    private bool running;
    private string? minted;

    public DeadLetterPlan Plan() => new(ExecuteConfirm, InspectLimit, recordDir);

    /// <summary>A parked queue's name, which is all the tool reads: an endpoint with its <c>_error</c> or <c>_skipped</c> suffix.</summary>
    public static bool IsParkedQueue(string queue) => ParkedName().IsMatch(queue);

    /// <summary>A message id the tool can be handed as an argument: no leading dash, so it can never read as a flag.</summary>
    public static bool IsMessageId(string id) => MessageIdShape().IsMatch(id);

    public static string[] SetPasswordArguments(string password) => ["rabbitmqctl", "change_password", Operator, password];

    /// <summary><c>inspect</c> with <c>--json</c>, the contract the tool's README fixes for this console.</summary>
    public static ProcessSpec InspectSpec(string queue, int limit, RepoPaths paths, string password) =>
        ToolSpec(["inspect", queue, "--limit", limit.ToString(CultureInfo.InvariantCulture), "--json"], paths, password);

    /// <summary>
    /// A replay or discard of the named messages: a dry run with no files, the real run with <c>--execute</c>, the
    /// record each taken message is written to first, and the audit log.
    /// </summary>
    public static ProcessSpec MoveSpec(
        DeadLetterVerb verb, string queue, IReadOnlyList<string> messageIds, (string Record, string AuditLog)? execute, RepoPaths paths, string password) =>
        ToolSpec(
            [
                Verb(verb),
                queue,
                .. messageIds.SelectMany(id => new[] { "--message-id", id }),
                .. execute is { } files ? ["--execute", "--record", files.Record, "--audit-log", files.AuditLog] : Array.Empty<string>(),
            ],
            paths,
            password);

    public static string Verb(DeadLetterVerb verb) => verb.ToString().ToLowerInvariant();

    /// <summary>Reads up to <see cref="InspectLimit"/> messages and puts them back; null while another run holds the operator.</summary>
    public async Task<DeadLetterInspection?> InspectAsync(string queue, CancellationToken cancellationToken)
    {
        if (!TryEnter())
        {
            return null;
        }

        try
        {
            string password = NewPassword();
            DeadLetterInspection inspection;
            string? stuck;

            try
            {
                inspection = await ReadAsync(queue, password, cancellationToken);
            }
            finally
            {
                stuck = (await ClearAsync()).Error;
            }

            return stuck is null ? inspection : inspection with { Error = Joined(inspection.Error, StillSet(stuck)) };
        }
        finally
        {
            Leave();
        }
    }

    private async Task<DeadLetterInspection> ReadAsync(string queue, string password, CancellationToken cancellationToken)
    {
        if ((await SetAsync(password, cancellationToken)).Error is { } refused)
        {
            return Failed(queue, $"The operator's password could not be set: {refused}");
        }

        Job read = runner.Start(InspectSpec(queue, InspectLimit, paths, password));
        CommandOutput output = await OneShot.CompleteAsync(runner, read, "dead_letters inspect", time, cancellationToken);

        return output.Error is { } error ? Failed(queue, error) : Parse(queue, output.Stdout);
    }

    private Task<CommandOutput> SetAsync(string password, CancellationToken cancellationToken) =>
        compose.ExecListedAsync("rabbitmq", SetPasswordArguments(password), Secret(password), cancellationToken);

    /// <summary>Never cancelled: a clear abandoned with its caller's request would leave the account with a password.</summary>
    private Task<CommandOutput> ClearAsync() =>
        compose.ExecListedAsync("rabbitmq", ClearPasswordArguments, new HashSet<string>(), CancellationToken.None);

    /// <summary>
    /// Starts a replay or discard as one listed job; null while another run holds the operator. The job carries the
    /// set, the tool and the clear, in order, as <see cref="ProjectionRebuildService"/>'s does its steps.
    /// </summary>
    public Job? Start(DeadLetterVerb verb, string queue, IReadOnlyList<string> messageIds, bool execute)
    {
        if (!TryEnter())
        {
            return null;
        }

        try
        {
            Job job = registry.Create(new ProcessSpec("dead_letters", [Verb(verb), queue, .. execute ? [ExecuteConfirm] : Array.Empty<string>()], paths.BackendDir));
            _ = Task.Run(() => RunAsync(job, verb, queue, messageIds, execute));

            return job;
        }
        catch
        {
            Leave();
            throw;
        }
    }

    private async Task RunAsync(Job job, DeadLetterVerb verb, string queue, IReadOnlyList<string> messageIds, bool execute)
    {
        try
        {
            job.MarkExited(await MoveAsync(job, verb, queue, messageIds, execute));
        }
        catch (Exception ex)
        {
            job.Append(OutputStream.Stderr, $"dead-letter run failed: {ex.Message}");
            job.MarkExited(-1);
        }
        finally
        {
            Leave();
        }
    }

    private async Task<int> MoveAsync(Job job, DeadLetterVerb verb, string queue, IReadOnlyList<string> messageIds, bool execute)
    {
        string password = NewPassword();
        int exitCode;

        try
        {
            job.Append(OutputStream.Stdout, $"$ docker compose exec rabbitmq rabbitmqctl change_password {Operator} {FixtureScrubber.Scrubbed}");

            if ((await SetAsync(password, CancellationToken.None)).Error is { } refused)
            {
                job.Append(OutputStream.Stderr, $"The operator's password could not be set, so the tool did not run: {refused}");
                exitCode = 1;
            }
            else
            {
                exitCode = await ToolAsync(job, verb, queue, messageIds, execute, password);
            }
        }
        catch (Exception ex)
        {
            job.Append(OutputStream.Stderr, $"The tool could not run: {ex.Message}");
            exitCode = -1;
        }
        finally
        {
            job.Append(OutputStream.Stdout, $"$ docker compose exec rabbitmq {string.Join(' ', ClearPasswordArguments)}");

            if ((await ClearAsync()).Error is { } stuck)
            {
                job.Append(OutputStream.Stderr, StillSet(stuck));
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Runs the tool and copies its lines into the job. A stderr line the tool prefixes <see cref="UnrecordedPrefix"/> is
    /// the last copy of a message it took and could not record, so it is kept beside the record and named on screen.
    /// </summary>
    private async Task<int> ToolAsync(Job job, DeadLetterVerb verb, string queue, IReadOnlyList<string> messageIds, bool execute, string password)
    {
        (string Record, string AuditLog)? files = null;
        string? unrecorded = null;

        if (execute)
        {
            Directory.CreateDirectory(recordDir);
            string stem = Path.Combine(recordDir, $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{queue}-{Verb(verb)}");
            files = ($"{stem}.record.jsonl", Path.Combine(recordDir, "audit.jsonl"));
            unrecorded = $"{stem}.unrecorded.jsonl";
            job.Append(OutputStream.Stdout, $"Each message is written to {files.Value.Record} before it leaves {queue}; keep that file.");
        }

        Job step = runner.Start(MoveSpec(verb, queue, messageIds, files, paths, password));
        job.Append(OutputStream.Stdout, $"$ {step.Spec.CommandLine}");

        await foreach (OutputLine line in step.Follow(-1, CancellationToken.None))
        {
            job.Append(line.Stream, line.Text);

            if (line.Stream == OutputStream.Stderr && line.Text.StartsWith(UnrecordedPrefix, StringComparison.Ordinal) && unrecorded is not null)
            {
                // Inside the loop on purpose: leaving it would clear the password and free the operator under a tool
                // that is still moving messages, so a file that cannot be written is said and the job keeps the line.
                try
                {
                    await File.AppendAllTextAsync(unrecorded, line.Text[UnrecordedPrefix.Length..] + "\n");
                    job.Append(OutputStream.Stderr, $"The line above is the last copy of a message the tool could not record; it is kept in {unrecorded}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    job.Append(OutputStream.Stderr, $"The line above is the last copy of a message the tool could not record, and {unrecorded} could not be written ({ex.Message}): copy the line now.");
                }
            }
        }

        int exitCode = await step.Completion;

        if (exitCode != 0)
        {
            job.Append(OutputStream.Stderr, $"dead_letters {Verb(verb)} exited with {exitCode}.");
        }

        return exitCode;
    }

    private bool TryEnter()
    {
        lock (gate)
        {
            if (running)
            {
                return false;
            }

            running = true;
            return true;
        }
    }

    private void Leave()
    {
        lock (gate)
        {
            running = false;
            minted = null;
        }
    }

    /// <summary>
    /// The password minted for the run under way, until that run leaves; null between runs. The assistant's redactor
    /// reads it, so the password never reaches a provider while one exists (AI assistant design §10).
    /// </summary>
    public string? MintedPassword
    {
        get
        {
            lock (gate)
            {
                return minted;
            }
        }
    }

    private static ProcessSpec ToolSpec(string[] arguments, RepoPaths paths, string password) =>
        new(Python[0], [.. Python[1..], Tool, .. arguments], paths.BackendDir)
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { [PasswordVariable] = password },
            Secrets = Secret(password),
        };

    private static HashSet<string> Secret(string password) => new(StringComparer.Ordinal) { password };

    private string NewPassword()
    {
        string password = RandomNumberGenerator.GetHexString(32, lowercase: true);

        lock (gate)
        {
            minted = password;
        }

        return password;
    }

    private static string StillSet(string error) =>
        $"The operator's password could not be cleared ({error}); if it was set, it still is, so clear it with: docker compose exec rabbitmq {string.Join(' ', ClearPasswordArguments)}";

    private static string Joined(string? first, string second) => first is null ? second : $"{first} {second}";

    private static DeadLetterInspection Failed(string queue, string error) => new(queue, null, null, [], error);

    /// <summary>The tool's <c>inspect --json</c> document, schema 1; anything else is said rather than guessed at.</summary>
    internal static DeadLetterInspection Parse(string queue, IReadOnlyList<string> stdout)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.Join('\n', stdout));
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
            {
                return Failed(queue, "The tool's JSON is not schema 1, the one this console reads; see the tool's README, \"The JSON contract\".");
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                return Failed(queue, error.GetString() ?? "The tool refused.");
            }

            DeadLetterMessage[] messages = [.. root.GetProperty("messages").EnumerateArray().Select(m => new DeadLetterMessage(
                Text(m, "message_id"),
                Text(m, "correlation_id"),
                Text(m, "message_type"),
                Text(m, "fault_message"),
                Text(m, "fault_exception_type"),
                Text(m, "input_address"),
                Text(m, "reason"),
                m.TryGetProperty("redelivered", out JsonElement redelivered) && redelivered.ValueKind == JsonValueKind.True,
                Text(m, "body")))];

            return new DeadLetterInspection(queue, Text(root, "endpoint"), Text(root, "kind"), messages, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Failed(queue, $"The tool's output could not be read as its JSON contract: {ex.Message}");
        }
    }

    /// <summary>A string field, or the strings of an array joined, as MassTransit's <c>messageType</c> is one; null otherwise.</summary>
    private static string? Text(JsonElement element, string name) =>
        !element.TryGetProperty(name, out JsonElement value) ? null : value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
            _ => null,
        };

    // \z, not $: in .NET a $ also matches before a final newline, which would let "x_error\n" through to the tool's argv.
    [GeneratedRegex(@"^[a-z][a-z0-9-]*_(error|skipped)\z")]
    private static partial Regex ParkedName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z")]
    private static partial Regex MessageIdShape();
}
