using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Host.Config;

namespace Admin.Host.Fakes;

/// <summary>
/// Writes what <c>Admin:Record</c> captured into the fixture directory, scrubbed before a byte reaches the disk
/// (<see cref="FixtureScrubber"/>). Off unless asked for. A fixture is replaced whole, except one that holds an
/// answer per question, where a run replaces the answers it asked for; either way the diff is the review. A write
/// that fails is logged and never fails the call that produced the answer: recording rides along with the
/// console's own work.
/// </summary>
public sealed partial class FixtureRecorder
{
    /// <summary>LF whatever the platform, so recordings made on Windows and on Linux differ only in content.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    private readonly Lock gate = new();
    private readonly ILogger logger;

    private FixtureRecorder(string? directory, ILogger logger)
    {
        Directory = directory;
        this.logger = logger;
    }

    /// <summary>The resolved fixture directory, or null when recording is off.</summary>
    public string? Directory { get; }

    public bool On => Directory is not null;

    /// <summary>Validated once at startup, so a run asked to record what it cannot fails with the key to fix.</summary>
    public static FixtureRecorder From(AdminOptions options, string baseDir, ILogger logger)
    {
        if (!options.Record)
        {
            return new FixtureRecorder(null, logger);
        }

        if (options.FakePlatform)
        {
            throw new InvalidOperationException(
                "Admin:Record and Admin:FakePlatform are both true: the fakes answer from the fixtures, "
                + "so recording them would write the fixtures back to themselves.");
        }

        string directory = Path.GetFullPath(options.RecordDir, baseDir);

        return System.IO.Directory.Exists(directory)
            ? new FixtureRecorder(directory, logger)
            : throw new InvalidOperationException($"Admin:RecordDir resolves to '{directory}', which does not exist.");
    }

    /// <summary>
    /// Replaces <paramref name="fixture"/> with <paramref name="content"/>, scrubbed and ending in a newline: the
    /// services end their documents without one, and an answer that has not moved should record as no diff.
    /// </summary>
    public void Write(string fixture, string content)
    {
        if (Directory is null)
        {
            return;
        }

        string scrubbed = FixtureScrubber.Scrub(content);

        lock (gate)
        {
            Save(fixture, scrubbed.EndsWith('\n') ? scrubbed : scrubbed + "\n");
        }
    }

    /// <summary>
    /// Sets <paramref name="key"/> in a fixture that holds one JSON answer per key, keeping the others: a run that
    /// asked only some of the questions leaves the rest as they were recorded before. A held file that is not a
    /// JSON object is started afresh rather than left to refuse every later write.
    /// </summary>
    public void WriteKeyed(string fixture, string key, string json)
    {
        if (Directory is null)
        {
            return;
        }

        JsonNode? answer;

        try
        {
            answer = JsonNode.Parse(FixtureScrubber.Scrub(json));
        }
        catch (JsonException e)
        {
            LogNotRecordedUnder(fixture, key, e.Message);

            return;
        }

        lock (gate)
        {
            try
            {
                JsonObject recording = Held(Path.Combine(Directory, fixture));
                recording[key] = answer;
                Save(fixture, recording.ToJsonString(Indented) + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogNotRecordedUnder(fixture, key, e.Message);
            }
        }
    }

    private static JsonObject Held(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Written beside the target and moved over it, so a reader never sees half a fixture; a temporary file a
    /// failure leaves is removed, since the fixture gate refuses any file it cannot account for.
    /// </summary>
    private void Save(string fixture, string content)
    {
        string path = Path.Combine(Directory!, fixture);
        string temporary = path + ".recording";

        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
            LogRecorded(fixture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogNotRecorded(fixture, e.Message);
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do: the gate names the file if it stays.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded {Fixture}")]
    private partial void LogRecorded(string fixture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not recorded into {Fixture}: {Error}")]
    private partial void LogNotRecorded(string fixture, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not recorded into {Fixture} under {Key}: {Error}")]
    private partial void LogNotRecordedUnder(string fixture, string key, string error);
}
