using System.Threading.Channels;
using Magi.Api.Configuration;
using Magi.Api.Data;
using Magi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Magi.Api.Deliberations;

/// <summary>Hand-off point between the HTTP request that creates a deliberation and the background worker.</summary>
public sealed class DeliberationQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid id) => _channel.Writer.TryWrite(id);
}

/// <summary>
/// Runs queued deliberations in the background, with a cap on how many run at once
/// (each one fires up to six paid LLM calls).
/// </summary>
public sealed class DeliberationWorker(
    DeliberationQueue queue,
    DeliberationEngine engine,
    IServiceScopeFactory scopeFactory,
    MagiSettings settings,
    ILogger<DeliberationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MarkInterruptedAsync(stoppingToken);

        using var slots = new SemaphoreSlim(settings.MaxConcurrentDeliberations);
        var running = new List<Task>();

        try
        {
            await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await engine.RunAsync(id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        logger.LogInformation("Deliberation {Id} interrupted by shutdown", id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unhandled error running deliberation {Id}", id);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Deliberation worker stopping");
        }

        await Task.WhenAll(running);
    }

    /// <summary>The queue lives in memory: anything left pending or running by a previous process is lost.</summary>
    private async Task MarkInterruptedAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MagiDbContext>();
        var count = await db.Deliberations
            .Where(d => d.Status == DeliberationStatus.Pending || d.Status == DeliberationStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeliberationStatus.Failed)
                .SetProperty(d => d.FailureReason, "Interrupted by a server restart")
                .SetProperty(d => d.CompletedAt, DateTime.UtcNow), ct);

        if (count > 0)
        {
            logger.LogWarning("Marked {Count} interrupted deliberations as failed", count);
        }
    }
}
