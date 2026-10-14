namespace Jobs.Core.Domain;

public abstract class DomainException(string message) : Exception(message);

/// <summary>The job is in a state that does not allow the operation (HTTP 409).</summary>
public sealed class JobStateConflictException(string message) : DomainException(message);

/// <summary>The line does not exist on the job (HTTP 404).</summary>
public sealed class LineNotFoundException(Guid lineId)
    : DomainException($"Line '{lineId}' does not exist on this job.")
{
    public Guid LineId { get; } = lineId;
}

/// <summary>Input breaks a business rule (HTTP 400). Keys are JSON field names.</summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public DomainValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] })
    {
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
