using Microsoft.Extensions.Options;

namespace Jobs.Worker;

/// <summary>
/// Polling loop around <see cref="ExportProcessor"/>. Any number of instances can run:
/// SKIP LOCKED keeps them off each other's tasks and the lease token fences stale writes.
/// </summary>
public sealed class ExportWorker(ExportProcessor processor, IOptions<ExportWorkerOptions> options, ILogger<ExportWorker> logger)
    : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Export worker {WorkerId} started.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;
            try
            {
                claimed = await processor.ProcessBatchAsync(_workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Export loop failed; trying again after the poll interval.");
                claimed = 0;
            }

            // A non-empty batch means there may be more work: claim again straight away.
            if (claimed > 0)
            {
                continue;
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Export worker {WorkerId} stopped claiming; in-flight exports have finished.", _workerId);
    }
}
