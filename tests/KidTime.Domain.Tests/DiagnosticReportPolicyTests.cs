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

    /// <summary>
    /// The agent's queue is keyed by report id and its repeat suppression is in memory, so a
    /// batch uploaded after a service restart carries the same fault more than once. Storing it
    /// twice violates the one-row-per-fingerprint index and used to fail the whole upload, which
    /// the agent then retried forever - so no fault from that PC ever reached the parent again.
    /// </summary>
    [Fact]
    public void Repeats_within_one_batch_become_one_entry_that_counts_them_all()
    {
        var first = Report("The tray agent recovered from an error. Attempt 1.") with
        {
            OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var second = Report("The tray agent recovered from an error. Attempt 2.") with
        {
            OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4)
        };
        var third = Report("The tray agent recovered from an error. Attempt 37.") with
        {
            OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        var collapsed = DiagnosticReportPolicy.CollapseBatch([first, second, third]);

        var only = Assert.Single(collapsed);
        Assert.Equal(3, only.Occurrences);
        Assert.Equal(first.OccurredAtUtc, only.FirstOccurredAtUtc);
        Assert.Equal(third.OccurredAtUtc, only.LastOccurredAtUtc);
        Assert.Equal(third.ReportId, only.Report.ReportId);
        Assert.Equal(third.Message, only.Report.Message);
    }

    [Fact]
    public void Distinct_faults_stay_distinct_and_keep_the_order_they_arrived_in()
    {
        var pipe = Report("The named pipe exchange failed.");
        var tray = Report("The tray icon could not be registered.");

        var collapsed = DiagnosticReportPolicy.CollapseBatch([pipe, tray, pipe with { ReportId = Guid.NewGuid() }]);

        Assert.Equal(2, collapsed.Count);
        Assert.Equal(DiagnosticReportPolicy.CreateFingerprint(pipe), collapsed[0].Fingerprint);
        Assert.Equal(2, collapsed[0].Occurrences);
        Assert.Equal(DiagnosticReportPolicy.CreateFingerprint(tray), collapsed[1].Fingerprint);
        Assert.Equal(1, collapsed[1].Occurrences);
    }

    [Fact]
    public void The_same_report_id_twice_is_one_occurrence_being_retried()
    {
        var report = Report("The tray agent stopped.");

        var collapsed = DiagnosticReportPolicy.CollapseBatch([report, report, report]);

        Assert.Equal(1, Assert.Single(collapsed).Occurrences);
    }

    [Fact]
    public void An_oversized_batch_is_bounded_before_anything_is_stored()
    {
        var reports = Enumerable
            .Range(0, DiagnosticReportPolicy.MaximumReportsPerBatch + 20)
            .Select(index => Report($"Distinct fault kind {(char)('a' + index % 26)}{index / 26}"))
            .ToList();

        var collapsed = DiagnosticReportPolicy.CollapseBatch(reports);

        Assert.True(collapsed.Count <= DiagnosticReportPolicy.MaximumReportsPerBatch);
        Assert.Equal(
            DiagnosticReportPolicy.MaximumReportsPerBatch,
            collapsed.Sum(item => item.Occurrences));
    }

    [Fact]
    public void An_empty_batch_collapses_to_nothing()
    {
        Assert.Empty(DiagnosticReportPolicy.CollapseBatch([]));
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
