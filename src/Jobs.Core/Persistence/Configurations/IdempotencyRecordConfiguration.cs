using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobs.Core.Persistence.Configurations;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(r => r.Key).HasName(IdempotencyConstraints.PrimaryKey);

        builder.Property(r => r.Key).HasMaxLength(IdempotencyRecord.KeyMaxLength);
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();

        // text, not jsonb: jsonb reorders keys, and a replay must be byte-identical to the first response.
        builder.Property(r => r.ResponseBody).HasColumnType("text").IsRequired();
        builder.Property(r => r.ResponseEtag).HasMaxLength(32).IsRequired();
    }
}

public static class IdempotencyConstraints
{
    /// <summary>Name of the primary key whose unique violation means "this key was already used".</summary>
    public const string PrimaryKey = "pk_idempotency_keys";
}
