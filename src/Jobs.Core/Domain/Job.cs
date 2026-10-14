namespace Jobs.Core.Domain;

/// <summary>
/// Aggregate root for a piece of field work. Every change, including changes to lines,
/// validates state and bumps <see cref="Version"/>, which is the optimistic concurrency token
/// and the HTTP ETag.
/// </summary>
public sealed class Job
{
    public const int CustomerNameMaxLength = 200;
    public const int SiteAddressMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    private readonly List<JobLine> _lines = [];

    // For EF Core.
    private Job()
    {
        CustomerName = null!;
        SiteAddress = null!;
        Description = null!;
    }

    private Job(Guid id, string customerName, string siteAddress, string description, DateTimeOffset now)
    {
        Id = id;
        CustomerName = customerName;
        SiteAddress = siteAddress;
        Description = description;
        Status = JobStatus.Draft;
        Version = 1;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }

    public string CustomerName { get; private set; }

    public string SiteAddress { get; private set; }

    public string Description { get; private set; }

    public JobStatus Status { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<JobLine> Lines => _lines;

    public ExportTask? Export { get; private set; }

    public decimal Total => _lines.Sum(line => line.Total);

    public static Job Create(string? customerName, string? siteAddress, string? description, DateTimeOffset now)
    {
        var errors = new ValidationErrors();
        var customer = InputRules.Text(errors, "customerName", customerName, CustomerNameMaxLength, required: true);
        var site = InputRules.Text(errors, "siteAddress", siteAddress, SiteAddressMaxLength, required: true);
        var text = InputRules.Text(errors, "description", description, DescriptionMaxLength, required: false);
        errors.ThrowIfAny();

        return new Job(Guid.CreateVersion7(), customer, site, text, now);
    }

    /// <summary>Changes the job header. A null argument leaves that field unchanged.</summary>
    public void Update(string? customerName, string? siteAddress, string? description, DateTimeOffset now)
    {
        EnsureEditable();

        if (customerName is null && siteAddress is null && description is null)
        {
            throw new DomainValidationException("body", "At least one field must be provided.");
        }

        var errors = new ValidationErrors();
        var customer = customerName is null
            ? CustomerName
            : InputRules.Text(errors, "customerName", customerName, CustomerNameMaxLength, required: true);
        var site = siteAddress is null
            ? SiteAddress
            : InputRules.Text(errors, "siteAddress", siteAddress, SiteAddressMaxLength, required: true);
        var text = description is null
            ? Description
            : InputRules.Text(errors, "description", description, DescriptionMaxLength, required: false);
        errors.ThrowIfAny();

        CustomerName = customer;
        SiteAddress = site;
        Description = text;
        Touch(now);
    }

    /// <summary>Adds a line. The first line on a Draft job moves it to InProgress.</summary>
    public JobLine AddLine(LineType? type, string? description, decimal? quantity, decimal? unitPrice, DateTimeOffset now)
    {
        EnsureEditable();

        var errors = new ValidationErrors();
        var lineType = InputRules.Type(errors, type);
        var text = InputRules.Text(errors, "description", description, JobLine.DescriptionMaxLength, required: true);
        var qty = InputRules.Quantity(errors, quantity);
        var price = InputRules.UnitPrice(errors, unitPrice);
        errors.ThrowIfAny();

        var line = new JobLine(Guid.CreateVersion7(), Id, lineType, text, qty, price, now);
        _lines.Add(line);

        if (Status == JobStatus.Draft)
        {
            Status = JobStatus.InProgress;
        }

        Touch(now);
        return line;
    }

    /// <summary>Changes a line. A null argument leaves that field unchanged.</summary>
    public void UpdateLine(Guid lineId, LineType? type, string? description, decimal? quantity, decimal? unitPrice, DateTimeOffset now)
    {
        EnsureEditable();
        var line = FindLine(lineId);

        if (type is null && description is null && quantity is null && unitPrice is null)
        {
            throw new DomainValidationException("body", "At least one field must be provided.");
        }

        var errors = new ValidationErrors();
        var lineType = type is null ? line.Type : InputRules.Type(errors, type);
        var text = description is null
            ? line.Description
            : InputRules.Text(errors, "description", description, JobLine.DescriptionMaxLength, required: true);
        var qty = quantity is null ? line.Quantity : InputRules.Quantity(errors, quantity);
        var price = unitPrice is null ? line.UnitPrice : InputRules.UnitPrice(errors, unitPrice);
        errors.ThrowIfAny();

        line.Update(lineType, text, qty, price);
        Touch(now);
    }

    /// <summary>Removes a line. The status stays as it is, so an InProgress job can end up with no lines.</summary>
    public void RemoveLine(Guid lineId, DateTimeOffset now)
    {
        EnsureEditable();
        var line = FindLine(lineId);

        _lines.Remove(line);
        Touch(now);
    }

    /// <summary>Freezes the job and queues its invoice export in the same unit of work.</summary>
    public ExportTask Complete(DateTimeOffset now)
    {
        if (Status != JobStatus.InProgress)
        {
            throw new JobStateConflictException($"Only an InProgress job can be completed; this job is {Status}.");
        }

        if (_lines.Count == 0)
        {
            throw new JobStateConflictException("A job needs at least one line before it can be completed.");
        }

        var export = ExportTask.Create(Id, now);
        Status = JobStatus.Completed;
        CompletedAt = now;
        Export = export;
        Touch(now);
        return export;
    }

    public void MarkExported(DateTimeOffset now)
    {
        EnsureCompleted();
        Status = JobStatus.Exported;
        Touch(now);
    }

    public void MarkExportFailed(DateTimeOffset now)
    {
        EnsureCompleted();
        Status = JobStatus.ExportFailed;
        Touch(now);
    }

    private JobLine FindLine(Guid lineId)
        => _lines.FirstOrDefault(line => line.Id == lineId) ?? throw new LineNotFoundException(lineId);

    private void EnsureEditable()
    {
        if (Status is not (JobStatus.Draft or JobStatus.InProgress))
        {
            throw new JobStateConflictException($"The job is {Status} and can no longer be changed.");
        }
    }

    private void EnsureCompleted()
    {
        if (Status != JobStatus.Completed)
        {
            throw new JobStateConflictException($"Only a Completed job can record an export outcome; this job is {Status}.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
