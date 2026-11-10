using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sms.Core.Application;

namespace Sms.Infrastructure;

/// <summary>Runs <see cref="DispatchPending"/> every IntervalSeconds in its own DI scope (fresh DbContext per tick).</summary>
internal sealed class DispatcherWorker(
    IServiceScopeFactory scopes, DispatcherOptions options, TimeProvider time, ILogger<DispatcherWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds), time);
        do
        {
            await DispatchOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DispatchOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DispatchPending>().ExecuteAsync(stoppingToken);
        }
        catch (ConcurrencyConflictException exception)
        {
            logger.LogWarning(exception, "Dispatch tick skipped: messages were changed concurrently, retrying next tick");
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Dispatch tick failed, retrying next tick");
        }
    }
}
