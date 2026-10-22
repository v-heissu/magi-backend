using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Magi.Api.Deliberations;

/// <param name="Type">SSE event name (snapshot, round_started, core_started, core_voted, round_completed, resolved, failed).</param>
public sealed record MagiEvent(string Type, object Data);

/// <summary>
/// In-memory fan-out of live deliberation events to SSE subscribers.
/// Every stream keeps its full history, so a client that connects late (or reconnects)
/// receives everything from the initial snapshot onward and ends up in the same state.
/// Finished streams are kept for a short grace period, then the database is the source of truth.
/// </summary>
public sealed class DeliberationEventHub
{
    private static readonly TimeSpan RetentionAfterCompletion = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, LiveStream> _streams = new();

    public void Open(Guid id, MagiEvent initial)
    {
        var stream = new LiveStream();
        stream.History.Add(initial);
        _streams[id] = stream;
    }

    public void Publish(Guid id, MagiEvent evt)
    {
        if (!_streams.TryGetValue(id, out var stream))
        {
            return;
        }

        lock (stream.Lock)
        {
            if (stream.Completed)
            {
                return;
            }

            stream.History.Add(evt);
            foreach (var subscriber in stream.Subscribers)
            {
                subscriber.Writer.TryWrite(evt);
            }
        }
    }

    public void Complete(Guid id)
    {
        if (!_streams.TryGetValue(id, out var stream))
        {
            return;
        }

        lock (stream.Lock)
        {
            stream.Completed = true;
            foreach (var subscriber in stream.Subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            stream.Subscribers.Clear();
        }

        _ = RemoveLaterAsync(id, stream);
    }

    /// <summary>
    /// Subscribes to a live stream. The returned reader first yields the whole history, then live events,
    /// and completes when the deliberation ends. Returns null when no live stream exists.
    /// </summary>
    public Subscription? Subscribe(Guid id)
    {
        if (!_streams.TryGetValue(id, out var stream))
        {
            return null;
        }

        var channel = Channel.CreateUnbounded<MagiEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (stream.Lock)
        {
            foreach (var evt in stream.History)
            {
                channel.Writer.TryWrite(evt);
            }

            if (stream.Completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                stream.Subscribers.Add(channel);
            }
        }

        return new Subscription(channel.Reader, () =>
        {
            lock (stream.Lock)
            {
                stream.Subscribers.Remove(channel);
            }
        });
    }

    private async Task RemoveLaterAsync(Guid id, LiveStream stream)
    {
        await Task.Delay(RetentionAfterCompletion);
        // Only remove the instance we completed, in case the id was reopened in the meantime.
        _streams.TryRemove(new KeyValuePair<Guid, LiveStream>(id, stream));
    }

    private sealed class LiveStream
    {
        public readonly object Lock = new();
        public readonly List<MagiEvent> History = [];
        public readonly List<Channel<MagiEvent>> Subscribers = [];
        public bool Completed;
    }
}

public sealed class Subscription(ChannelReader<MagiEvent> reader, Action unsubscribe) : IDisposable
{
    public ChannelReader<MagiEvent> Reader { get; } = reader;

    public void Dispose() => unsubscribe();
}
