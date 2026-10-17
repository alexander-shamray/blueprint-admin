using Admin.Host.Compose;
using Admin.Host.Frontend;
using Admin.Host.Stack;
using Shouldly;

namespace Admin.Host.Tests.Frontend;

public sealed class DeviceReadsTests
{
    private static CommandOutput Out(params string[] lines) => CommandOutput.Answered(lines);

    [Fact]
    public void An_attached_emulator_is_ok_and_the_daemons_notice_is_not_a_device()
    {
        DoctorCheck check = DeviceReads.Emulator(Out("* daemon started successfully", "List of devices attached", "emulator-5554\tdevice", ""));

        check.State.ShouldBe(DoctorState.Ok);
        check.Detail.ShouldContain("emulator-5554");
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("unauthorized")]
    public void A_device_that_is_not_ready_is_a_problem_even_beside_a_ready_one(string state)
    {
        DoctorCheck check = DeviceReads.Emulator(Out("List of devices attached", "emulator-5554\tdevice", $"emulator-5556\t{state}"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain($"emulator-5556 is {state}");
    }

    [Fact]
    public void No_device_line_says_to_start_the_avd_by_hand()
    {
        DoctorCheck check = DeviceReads.Emulator(Out("List of devices attached", ""));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("Device Manager");
    }

    [Fact]
    public void An_adb_that_does_not_answer_is_a_problem_because_no_device_run_can_start()
    {
        DeviceReads.Emulator(CommandOutput.Failed("adb.exe is not recognized")).State.ShouldBe(DoctorState.Problem);
    }

    [Fact]
    public void The_gateway_echoing_the_apps_origin_is_ok()
    {
        DoctorCheck check = DeviceReads.Origin(Out("HTTP/1.1 204 No Content", "Access-Control-Allow-Origin: https://localhost"));

        check.State.ShouldBe(DoctorState.Ok);
    }

    [Fact]
    public void A_preflight_answered_without_the_header_is_a_problem_raised_in_the_backend()
    {
        DoctorCheck check = DeviceReads.Origin(Out("HTTP/1.1 204 No Content"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("backend's to change");
    }

    [Fact]
    public void A_different_origin_in_the_header_is_a_problem_naming_it()
    {
        DoctorCheck check = DeviceReads.Origin(Out("HTTP/1.1 204 No Content", "Access-Control-Allow-Origin: http://localhost:5173"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("http://localhost:5173");
    }

    [Fact]
    public void A_gateway_that_printed_nothing_is_unknown_not_a_mismatch()
    {
        DeviceReads.Origin(Out()).State.ShouldBe(DoctorState.Unknown);
        DeviceReads.Origin(CommandOutput.Failed("powershell exited with 1")).State.ShouldBe(DoctorState.Unknown);
    }

    private const string Marker = "--- webOrigins ---";

    [Fact]
    public void The_one_redirect_the_client_sends_is_ok()
    {
        DeviceReads.Redirect(Out("blueprint://auth/callback", Marker, "https://localhost,capacitor://localhost")).State.ShouldBe(DoctorState.Ok);
    }

    [Fact]
    public void The_one_redirect_is_ok_when_the_origins_line_was_dropped_as_blank()
    {
        DeviceReads.Redirect(Out("blueprint://auth/callback", Marker)).State.ShouldBe(DoctorState.Ok);
    }

    [Fact]
    public void A_redirect_that_is_not_the_clients_is_a_problem_naming_it()
    {
        DoctorCheck check = DeviceReads.Redirect(Out("com.example://callback", Marker, "https://localhost"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain("com.example://callback");
    }

    [Fact]
    public void An_extra_redirect_beside_the_right_one_is_not_exactly_the_clients()
    {
        DeviceReads.Redirect(Out("blueprint://auth/callback", "http://localhost:5173/*", Marker, "https://localhost")).State.ShouldBe(DoctorState.Problem);
    }

    [Theory]
    [InlineData("com.example://app")]
    [InlineData("https://localhost")]
    public void A_path_less_redirect_beside_the_right_one_is_a_redirect_whatever_it_looks_like(string extra)
    {
        DoctorCheck check = DeviceReads.Redirect(Out("blueprint://auth/callback", extra, Marker, "https://localhost,capacitor://localhost"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain(extra);
    }

    [Fact]
    public void A_client_whose_origins_lack_the_apps_is_not_misread_as_a_redirect()
    {
        DeviceReads.Redirect(Out("blueprint://auth/callback", Marker, "capacitor://localhost")).State.ShouldBe(DoctorState.Ok);
    }

    [Fact]
    public void A_client_with_no_redirect_prints_only_the_marker_and_its_origins_and_is_a_problem()
    {
        DeviceReads.Redirect(Out(Marker, "https://localhost,capacitor://localhost")).State.ShouldBe(DoctorState.Problem);
        DeviceReads.Redirect(Out()).State.ShouldBe(DoctorState.Problem);
    }

    [Fact]
    public void Output_with_no_marker_is_a_problem_naming_it_and_not_a_guess_at_the_redirects()
    {
        DoctorCheck check = DeviceReads.Redirect(Out("blueprint://auth/callback", "https://localhost"));

        check.State.ShouldBe(DoctorState.Problem);
        check.Detail.ShouldContain(Marker);
    }

    [Fact]
    public void A_keycloak_that_did_not_answer_is_unknown()
    {
        DeviceReads.Redirect(CommandOutput.Failed("Unable to connect to the remote server")).State.ShouldBe(DoctorState.Unknown);
    }
}
