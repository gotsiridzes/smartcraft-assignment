using Jobs.Core.Persistence;
using Jobs.Worker.Accounting;
using Microsoft.EntityFrameworkCore;

namespace Jobs.Worker;

public static class InvoiceSnapshot
{
    /// <summary>Reads what gets invoiced. The job is frozen once completed, so the snapshot cannot drift between attempts.</summary>
    public static async Task<InvoiceRequest> LoadAsync(JobsDbContext db, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Include(j => j.Lines)
            .SingleAsync(j => j.Id == jobId, cancellationToken);

        var lines = job.Lines
            .OrderBy(line => line.CreatedAt)
            .ThenBy(line => line.Id)
            .Select(line => new InvoiceLine(line.Type.ToString(), line.Description, line.Quantity, line.UnitPrice, line.Total))
            .ToList();

        return new InvoiceRequest(job.Id, job.CustomerName, job.SiteAddress, lines, job.Total);
    }
}
