using Jobs.Core.Domain;

namespace Jobs.Tests.Domain;

/// <summary>Test 5: the state machine and the rules on the aggregate root.</summary>
public sealed class JobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_job_is_a_draft_at_version_1()
    {
        var job = NewJob();

        job.Status.ShouldBe(JobStatus.Draft);
        job.Version.ShouldBe(1);
        job.Lines.ShouldBeEmpty();
        job.Export.ShouldBeNull();
        job.Total.ShouldBe(0m);
    }

    [Fact]
    public void Creating_a_job_reports_every_invalid_field()
    {
        var error = Should.Throw<DomainValidationException>(() => Job.Create(" ", null, new string('x', 2001), Now));

        error.Errors.Keys.ShouldBe(new[] { "customerName", "siteAddress", "description" }, ignoreOrder: true);
    }

    [Fact]
    public void The_first_line_moves_a_draft_to_in_progress()
    {
        var job = NewJob();

        job.AddLine(LineType.Labour, "Electrician hours", 2.5m, 650m, Now);

        job.Status.ShouldBe(JobStatus.InProgress);
        job.Version.ShouldBe(2);
        job.Total.ShouldBe(1625.00m);
    }

    [Fact]
    public void Every_change_bumps_the_version_once()
    {
        var job = NewJob();

        job.Update("New customer", null, null, Now);
        job.Version.ShouldBe(2);

        var line = job.AddLine(LineType.Material, "Cable", 10m, 12.35m, Now);
        job.Version.ShouldBe(3);

        job.UpdateLine(line.Id, null, null, 20m, null, Now);
        job.Version.ShouldBe(4);

        job.RemoveLine(line.Id, Now);
        job.Version.ShouldBe(5);
    }

    [Fact]
    public void Editing_and_removing_lines_recalculates_the_total()
    {
        var job = NewJob();
        var labour = job.AddLine(LineType.Labour, "Electrician hours", 2m, 650m, Now);
        var material = job.AddLine(LineType.Material, "Cable", 10m, 12.35m, Now);
        job.Total.ShouldBe(1423.50m);

        job.UpdateLine(labour.Id, null, null, 3m, null, Now);
        job.Total.ShouldBe(2073.50m);

        job.RemoveLine(material.Id, Now);
        job.Total.ShouldBe(1950.00m);
    }

    [Fact]
    public void Line_totals_round_half_away_from_zero()
    {
        var job = NewJob();

        // 1.5 × 0.15 = 0.225: banker's rounding would give 0.22, invoices expect 0.23.
        job.AddLine(LineType.Material, "Screws", 1.5m, 0.15m, Now);

        job.Total.ShouldBe(0.23m);
    }

    [Fact]
    public void A_line_with_too_many_decimals_or_a_non_positive_quantity_is_rejected()
    {
        var job = NewJob();

        var error = Should.Throw<DomainValidationException>(() => job.AddLine(LineType.Material, "Cable", 0m, 1.234m, Now));

        error.Errors.Keys.ShouldBe(new[] { "quantity", "unitPrice" }, ignoreOrder: true);
        job.Version.ShouldBe(1);
        job.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_line_id_is_reported()
    {
        var job = NewJob();
        job.AddLine(LineType.Labour, "Electrician hours", 1m, 650m, Now);

        Should.Throw<LineNotFoundException>(() => job.RemoveLine(Guid.NewGuid(), Now));
        Should.Throw<LineNotFoundException>(() => job.UpdateLine(Guid.NewGuid(), null, "Renamed", null, null, Now));
    }

    [Fact]
    public void A_draft_job_cannot_be_completed()
    {
        var job = NewJob();

        Should.Throw<JobStateConflictException>(() => job.Complete(Now));
        job.Status.ShouldBe(JobStatus.Draft);
    }

    [Fact]
    public void Removing_the_last_line_blocks_completion()
    {
        var job = NewJob();
        var line = job.AddLine(LineType.Labour, "Electrician hours", 1m, 650m, Now);
        job.RemoveLine(line.Id, Now);

        job.Status.ShouldBe(JobStatus.InProgress);
        Should.Throw<JobStateConflictException>(() => job.Complete(Now));
    }

    [Fact]
    public void Completing_freezes_the_job_and_queues_one_export()
    {
        var job = InProgressJob(out _);
        var versionBefore = job.Version;

        var export = job.Complete(Now);

        job.Status.ShouldBe(JobStatus.Completed);
        job.CompletedAt.ShouldBe(Now);
        job.Version.ShouldBe(versionBefore + 1);
        job.Export.ShouldBeSameAs(export);
        export.JobId.ShouldBe(job.Id);
        export.Status.ShouldBe(ExportTaskStatus.Pending);
        export.Attempts.ShouldBe(0);
    }

    [Fact]
    public void A_completed_job_rejects_every_change()
    {
        var job = InProgressJob(out var line);
        job.Complete(Now);
        var version = job.Version;

        Should.Throw<JobStateConflictException>(() => job.Update("Someone else", null, null, Now));
        Should.Throw<JobStateConflictException>(() => job.AddLine(LineType.Labour, "Extra hours", 1m, 650m, Now));
        Should.Throw<JobStateConflictException>(() => job.UpdateLine(line.Id, null, null, 5m, null, Now));
        Should.Throw<JobStateConflictException>(() => job.RemoveLine(line.Id, Now));
        Should.Throw<JobStateConflictException>(() => job.Complete(Now));

        job.Version.ShouldBe(version);
    }

    [Fact]
    public void Export_outcomes_are_recorded_only_on_a_completed_job()
    {
        var draft = NewJob();
        Should.Throw<JobStateConflictException>(() => draft.MarkExported(Now));

        var exported = InProgressJob(out _);
        exported.Complete(Now);
        exported.MarkExported(Now);
        exported.Status.ShouldBe(JobStatus.Exported);
        Should.Throw<JobStateConflictException>(() => exported.MarkExportFailed(Now));

        var failed = InProgressJob(out _);
        failed.Complete(Now);
        failed.MarkExportFailed(Now);
        failed.Status.ShouldBe(JobStatus.ExportFailed);
    }

    [Fact]
    public void An_update_without_fields_is_rejected()
    {
        var job = NewJob();

        Should.Throw<DomainValidationException>(() => job.Update(null, null, null, Now));
        job.Version.ShouldBe(1);
    }

    private static Job NewJob() => Job.Create("Fjord Elektro AS", "Storgata 1, 0155 Oslo", "Rewire kitchen", Now);

    private static Job InProgressJob(out JobLine line)
    {
        var job = NewJob();
        line = job.AddLine(LineType.Labour, "Electrician hours", 2m, 650m, Now);
        return job;
    }
}
