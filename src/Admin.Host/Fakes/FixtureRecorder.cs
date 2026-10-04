using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Host.Config;

namespace Admin.Host.Fakes;

/// <summary>
/// Writes what <c>Admin:Record</c> captured into the fixture directory, scrubbed before a byte reaches the disk
/// (<see cref="FixtureScrubber"/>). Off unless asked for, and then a file is replaced whole, so a recording run
/// leaves the fixtures as the platform answered and the diff is the review. A write that fails is logged and never
/// fails the call that produced the answer: recording rides along with the console's own work.
/// </summary>
public sealed partial class FixtureRecorder
{
    /// <summary>LF whatever the platform, so a recording made on Windows and one made on Linux differ only in content.</summary>
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
                "Admin:Record and Admin:FakePlatform are both true: the fakes answer from the fixtures, so recording them would write the fixtures back to themselves.");
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
    /// asked only some of the questions leaves the rest as they were recorded before.
    /// </summary>
    public void WriteKeyed(string fixture, string key, string json)
    {
        if (Directory is null)
        {
            return;
        }

        lock (gate)
        {
            try
            {
                string path = Path.Combine(Directory, fixture);
                JsonObject recording = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject held ? held : [];
                recording[key] = JsonNode.Parse(FixtureScrubber.Scrub(json));
                Save(fixture, recording.ToJsonString(Indented) + "\n");
            }
            catch (JsonException e)
            {
                LogNotRecordedUnder(fixture, key, e.Message);
            }
        }
    }

    /// <summary>Written beside the target and moved over it, so a reader never sees half a fixture.</summary>
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
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded {Fixture}")]
    private partial void LogRecorded(string fixture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not recorded into {Fixture}: {Error}")]
    private partial void LogNotRecorded(string fixture, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not recorded into {Fixture} under {Key}: {Error}")]
    private partial void LogNotRecordedUnder(string fixture, string key, string error);
}
