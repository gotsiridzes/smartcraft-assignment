using Jobs.Core.Domain;

namespace Jobs.Api.Contracts;

// Request fields are nullable on purpose: the domain owns every rule, including "required",
// and PATCH requests use null to mean "leave unchanged".

public sealed record CreateJobRequest(string? CustomerName, string? SiteAddress, string? Description);

public sealed record UpdateJobRequest(string? CustomerName, string? SiteAddress, string? Description);

public sealed record AddLineRequest(LineType? Type, string? Description, decimal? Quantity, decimal? UnitPrice);

public sealed record UpdateLineRequest(LineType? Type, string? Description, decimal? Quantity, decimal? UnitPrice);

public sealed record JobResponse(
    Guid Id,
    string CustomerName,
    string SiteAddress,
    string Description,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<JobLineResponse> Lines,
    decimal Total,
    ExportStatusResponse? Export)
{
    public static JobResponse From(Job job) => new(
        job.Id,
        job.CustomerName,
        job.SiteAddress,
        job.Description,
        job.Status,
        job.CreatedAt,
        job.UpdatedAt,
        job.CompletedAt,
        job.Lines
            .OrderBy(line => line.CreatedAt)
            .ThenBy(line => line.Id)
            .Select(JobLineResponse.From)
            .ToList(),
        job.Total,
        job.Export is null ? null : ExportStatusResponse.From(job.Export));
}

public sealed record JobLineResponse(Guid Id, LineType Type, string Description, decimal Quantity, decimal UnitPrice, decimal Total)
{
    public static JobLineResponse From(JobLine line)
        => new(line.Id, line.Type, line.Description, line.Quantity, line.UnitPrice, line.Total);
}

public sealed record ExportStatusResponse(
    ExportTaskStatus Status,
    int Attempts,
    string? LastError,
    string? ExternalInvoiceId,
    DateTimeOffset? CompletedAt)
{
    public static ExportStatusResponse From(ExportTask task)
        => new(task.Status, task.Attempts, task.LastError, task.ExternalInvoiceId, task.CompletedAt);
}
