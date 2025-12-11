using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Demo.Services
{
    public interface IBackgroundTaskQueue
    {
        ValueTask QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem);
        ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
    }

    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly Channel<Func<CancellationToken, Task>> _queue;

        public BackgroundTaskQueue(int capacity = 100)
        {
            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };
            _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(options);
        }

        public async ValueTask QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem)
        {
            if (workItem == null) throw new ArgumentNullException(nameof(workItem));
            await _queue.Writer.WriteAsync(workItem);
        }

        public async ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}