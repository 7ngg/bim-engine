using BimEngine.Core.Models;

namespace BimEngine.Core.Contracts;

/// <summary>
/// Broker-agnostic pub/sub abstraction for <see cref="GeometryCommand"/> messages.
///
/// The API publishes; a consumer reads the stream exposed by <see cref="ConsumeAsync"/>.
/// Only this interface is shared, so a future RabbitMqMessageQueue (or Azure Service Bus,
/// Kafka, etc.) can replace the in-memory implementation without any caller changing.
/// </summary>
public interface IMessageQueue
{
    /// <summary>Publish a command for downstream consumers.</summary>
    Task PublishAsync(GeometryCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream published commands as they arrive. A background consumer enumerates this to
    /// process work. With a real broker this would wrap the broker's consumer/ack loop.
    /// </summary>
    IAsyncEnumerable<GeometryCommand> ConsumeAsync(CancellationToken cancellationToken = default);
}
