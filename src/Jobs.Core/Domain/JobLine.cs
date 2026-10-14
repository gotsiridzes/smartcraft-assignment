namespace Jobs.Core.Domain;

/// <summary>A labour or material line. Changed only through <see cref="Job"/>, the aggregate root.</summary>
public sealed class JobLine
{
    public const int DescriptionMaxLength = 500;

    // For EF Core.
    private JobLine()
    {
        Description = null!;
    }

    internal JobLine(Guid id, Guid jobId, LineType type, string description, decimal quantity, decimal unitPrice, DateTimeOffset createdAt)
    {
        Id = id;
        JobId = jobId;
        Type = type;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    public LineType Type { get; private set; }

    public string Description { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Quantity × unit price, rounded per line as an invoice would be.</summary>
    public decimal Total => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    internal void Update(LineType type, string description, decimal quantity, decimal unitPrice)
    {
        Type = type;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }
}
