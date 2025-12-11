using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Demo.Services
{
    public class QueuedHostedService : BackgroundService
    {
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly ILogger<QueuedHostedService> _logger;

        public QueuedHostedService(IBackgroundTaskQueue taskQueue, ILogger<QueuedHostedService> logger)
        {
            _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("QueuedHostedService started.");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var workItem = await _taskQueue.DequeueAsync(stoppingToken);
                    try
                    {
                        await workItem(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error while executing queued work item.");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // shutting down
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error dequeuing work item.");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            _logger.LogInformation("QueuedHostedService stopping.");
        }
    }
}