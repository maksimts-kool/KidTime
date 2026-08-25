using KidTime.Domain.Contracts;

namespace KidTime.Domain.Tests;

public sealed class DiagnosticReportPolicyTests
{
    [Fact]
    public void Repeats_of_one_fault_share_a_fingerprint_even_when_numbers_differ()
    {
        var first = Report("Named pipe client process 4812 was rejected at 10:04:11.");
        var second = Report("Named pipe client process 9137 was rejected at 11:57:02.");

        Assert.Equal(
            DiagnosticReportPolicy.CreateFingerprint(first),
            DiagnosticReportPolicy.CreateFingerprint(second));
    }

    [Fact]
    public void Different_components_and_exceptions_are_separate_faults()
    {
        var agent = Report("The tray agent stopped.");
        var service = agent with { Component = DiagnosticComponents.ControlService };
        var other = agent with { ExceptionType = "System.IO.IOException" };

        var fingerprint = DiagnosticReportPolicy.CreateFingerprint(agent);
        Assert.NotEqual(fingerprint, DiagnosticReportPolicy.CreateFingerprint(service));
        Assert.NotEqual(fingerprint, DiagnosticReportPolicy.CreateFingerprint(other));
    }

    [Fact]
    public void Untrusted_fields_are_truncated_and_defaulted()
    {
        var normalized = DiagnosticReportPolicy.Normalize(new DiagnosticReport(
            Guid.Empty,
            default,
            new string('c', 500),
            "not-a-severity",
            new string('m', 5_000),
            new string('t', 5_000),
            new string('d', 40_000),
            new string('v', 500)));

        Assert.NotEqual(Guid.Empty, normalized.ReportId);
        Assert.NotEqual(default, normalized.OccurredAtUtc);
        Assert.Equal(DiagnosticReportPolicy.MaximumComponentLength, normalized.Component.Length);
        Assert.Equal(DiagnosticSeverities.Error, normalized.Severity);
        Assert.Equal(DiagnosticReportPolicy.MaximumMessageLength, normalized.Message.Length);
        Assert.Equal(DiagnosticReportPolicy.MaximumExceptionTypeLength, normalized.ExceptionType!.Length);
        Assert.Equal(DiagnosticReportPolicy.MaximumDetailLength, normalized.Detail!.Length);
        Assert.Equal(DiagnosticReportPolicy.MaximumVersionLength, normalized.AgentVersion!.Length);
    }

    [Fact]
    public void The_receiver_decides_which_component_a_report_belongs_to()
    {
        var claimed = Report("The tray agent stopped.") with { Component = DiagnosticComponents.ControlService };

        var normalized = DiagnosticReportPolicy.Normalize(claimed, DiagnosticComponents.SessionAgent);

        Assert.Equal(DiagnosticComponents.SessionAgent, normalized.Component);
    }

    [Fact]
    public void Blank_text_falls_back_instead_of_producing_an_empty_report()
    {
        var normalized = DiagnosticReportPolicy.Normalize(new DiagnosticReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "   ",
            "   ",
            "   "));

        Assert.Equal(DiagnosticComponents.SessionAgent, normalized.Component);
        Assert.Equal(DiagnosticSeverities.Error, normalized.Severity);
        Assert.Equal("Unspecified failure", normalized.Message);
        Assert.Null(normalized.Detail);
    }

    private static DiagnosticReport Report(string message) => new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        DiagnosticComponents.SessionAgent,
        DiagnosticSeverities.Error,
        message,
        "System.InvalidOperationException",
        "at KidTime.SessionAgent.PipeClient.ExchangeAsync()",
        "0.2.21");
}
