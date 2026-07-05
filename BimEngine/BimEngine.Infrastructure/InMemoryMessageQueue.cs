using System.Threading.Channels;
using BimEngine.Core.Contracts;
using BimEngine.Core.Models;

namespace BimEngine.Infrastructure;

/// <summary>
/// In-process <see cref="IMessageQueue"/> backed by a <see cref="Channel{T}"/>. Lets the whole
/// pipeline run with a single `dotnet run` and no external broker.
///
/// Register as a SINGLETON: the producer (API) and consumer (background service) must resolve
/// the same instance so they share the same channel. See composition root in Program.cs.
///
/// SEAM: a future RabbitMqMessageQueue implements the same <see cref="IMessageQueue"/> interface
/// (publish to an exchange in PublishAsync, wrap the consumer/ack loop in ConsumeAsync). No other
/// code changes — only the DI registration swaps.
/// </summary>
public sealed class InMemoryMessageQueue : IMessageQueue
{
    // Unbounded so PublishAsync never blocks the request thread in this PoC.
    // A real broker would apply backpressure / bounded queues instead.
    private readonly Channel<GeometryCommand> _channel =
        Channel.CreateUnbounded<GeometryCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public async Task PublishAsync(GeometryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _channel.Writer.WriteAsync(command, cancellationToken);
    }

    public IAsyncEnumerable<GeometryCommand> ConsumeAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
