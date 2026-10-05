using System.Text;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Tests.Identity;
using Admin.Host.Transcript;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Transcript;

public sealed class OperatorTranscriptTests
{
    private static readonly string Token = TokenServiceTests.Jwt("""{"preferred_username":"demo"}""");

    private readonly OperatorTranscript transcript = new(new FakeTimeProvider());

    /// <summary>Starts nothing: each job is returned running, for the test to end.</summary>
    private sealed class StubRunner : IProcessRunner
    {
        public Job Start(ProcessSpec spec) => new($"job-{spec.FileName}", spec, TimeProvider.System);

        public Task StopAsync(Job job, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void A_listed_process_is_kept_with_its_directory_and_reads_its_exit_once_it_has_one()
    {
        TranscribingProcessRunner runner = new(new StubRunner(), transcript);
        Job job = runner.Start(new ProcessSpec("docker", ["compose", "-f", "deploy/compose/docker-compose.yml", "up", "-d", "--wait"], "/work/backend"));

        TranscriptEntry running = transcript.Read().Entries.ShouldHaveSingleItem();
        running.Kind.ShouldBe(TranscriptKind.Process);
        running.Command.ShouldBe("docker compose -f deploy/compose/docker-compose.yml up -d --wait");
        running.WorkingDirectory.ShouldBe("/work/backend");
        running.ExitCode.ShouldBeNull();

        job.MarkExited(0);

        transcript.Read().Entries.ShouldHaveSingleItem().ExitCode.ShouldBe(0);
    }

    [Fact]
    public void A_read_a_screen_polls_is_left_out_by_the_line_api_jobs_draws()
    {
        TranscribingProcessRunner runner = new(new StubRunner(), transcript);

        runner.Start(new ProcessSpec("docker", ["compose", "ps", "-a", "--format", "json"], "/work/backend") { Listed = false });

        transcript.Read().Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("up", "up")]
    [InlineData("deploy/compose/docker-compose.yml", "deploy/compose/docker-compose.yml")]
    [InlineData("C:\\dev\\blueprint-backend", "'C:\\dev\\blueprint-backend'")]
    [InlineData("two words", "'two words'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("", "''")]
    public void A_word_the_shell_would_split_or_expand_is_single_quoted(string word, string quoted)
    {
        ShellLine.Quote(word).ShouldBe(quoted);
    }

    [Fact]
    public void The_curl_is_the_message_as_built_with_every_authorization_value_elided()
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "http://localhost:5000/api/v1/orders")
        {
            Content = new StringContent("""{"basketId":"b-1"}""", Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new("Bearer", Token);
        message.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic c2hvcnQ=");
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", "c-1");

        string curl = ShellLine.Curl(message, """{"basketId":"b-1"}""");

        curl.ShouldStartWith("curl -i -X POST http://localhost:5000/api/v1/orders ");
        curl.ShouldContain("-H 'Authorization: Bearer <scrubbed>'");
        curl.ShouldContain("-H 'Proxy-Authorization: Basic <scrubbed>'");
        curl.ShouldContain("-H 'X-Correlation-Id: c-1'");
        curl.ShouldContain("-H 'Content-Type: application/json; charset=utf-8'");
        curl.ShouldEndWith("""--data-raw '{"basketId":"b-1"}'""");
        curl.ShouldNotContain(Token);
        curl.ShouldNotContain("c2hvcnQ=");
    }

    [Fact]
    public void A_get_names_no_method_and_a_head_asks_for_headers_only()
    {
        using HttpRequestMessage get = new(HttpMethod.Get, "http://localhost:5000/api/v1/catalog/products");
        using HttpRequestMessage head = new(HttpMethod.Head, "http://localhost:5000/api/v1/catalog/products");

        ShellLine.Curl(get, null).ShouldBe("curl -i http://localhost:5000/api/v1/catalog/products");
        ShellLine.Curl(head, null).ShouldBe("curl -i --head http://localhost:5000/api/v1/catalog/products");
    }

    [Fact]
    public void A_secret_in_a_body_or_an_argument_is_scrubbed_before_it_is_kept()
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "http://localhost:5000/api/v1/orders");
        transcript.Request(ShellLine.Curl(message, """{"password":"hunter2","note":"ok"}"""), "demo", 201);
        new TranscribingProcessRunner(new StubRunner(), transcript).Start(new ProcessSpec("tool", ["--token", Token], "/work"));

        TranscriptView view = transcript.Read();

        view.Entries[0].Command.ShouldContain("""{"password":"<scrubbed>","note":"ok"}""");
        view.Entries[1].Command.ShouldBe("tool --token '<scrubbed>'");
    }

    [Fact]
    public void Past_its_capacity_the_oldest_entry_goes_and_is_counted()
    {
        for (int i = 0; i < OperatorTranscript.Capacity + 3; i++)
        {
            transcript.Request($"curl -i http://localhost:5000/{i}", null, 200);
        }

        TranscriptView view = transcript.Read();

        view.Entries.Count.ShouldBe(OperatorTranscript.Capacity);
        view.Dropped.ShouldBe(3);
        view.Entries[0].Sequence.ShouldBe(4);
        transcript.Script().ShouldContain($"# 3 earlier entries were dropped past the last {OperatorTranscript.Capacity}.");
    }

    [Fact]
    public void The_script_runs_each_process_in_its_directory_under_its_outcome()
    {
        TranscribingProcessRunner runner = new(new StubRunner(), transcript);
        runner.Start(new ProcessSpec("npm", ["start"], "C:\\dev\\blueprint-frontend")).MarkExited(1);
        transcript.Request("curl -i http://localhost:5000/api/v1/orders", "demo", 403);
        transcript.Request("curl -i http://localhost:5000/api/v1/catalog/products", null, null);

        string script = transcript.Script();

        script.ShouldStartWith("#!/usr/bin/env bash\n");
        script.ShouldContain("· exit 1\n(cd 'C:\\dev\\blueprint-frontend' && npm start)\n");
        script.ShouldContain("· as demo · HTTP 403\ncurl -i http://localhost:5000/api/v1/orders\n");
        script.ShouldContain("· anonymous · no answer\ncurl -i http://localhost:5000/api/v1/catalog/products\n");
    }

    /// <summary>
    /// The subject is what the copy button hands over, read by the rules the fixture gate reads with: a
    /// transcript holding a bearer token is a failed build, the same as a fixture holding one.
    /// </summary>
    [Fact]
    public void The_script_is_clean_by_the_fixture_gates_rules_whatever_went_into_it()
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "http://localhost:8080/realms/commerce/protocol/openid-connect/token")
        {
            Content = new StringContent("grant_type=password&username=demo&password=demo&client_secret=s3cret", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        message.Headers.Authorization = new("Bearer", Token);
        transcript.Request(ShellLine.Curl(message, "grant_type=password&username=demo&password=demo&client_secret=s3cret"), "demo", 200);
        new TranscribingProcessRunner(new StubRunner(), transcript).Start(new ProcessSpec("curl", ["-H", $"Authorization: Bearer {Token}"], "/work"));

        string script = transcript.Script();

        FixtureScrubber.Findings(script).ShouldBeEmpty();
        script.ShouldNotContain(Token);
        script.ShouldNotContain("s3cret");
        script.ShouldNotContain("password=demo");
    }
}
