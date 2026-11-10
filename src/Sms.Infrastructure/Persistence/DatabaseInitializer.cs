using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Sms.Infrastructure.Persistence;

/// <summary>Runs once at startup, before the dispatcher (hosted services start in registration order).</summary>
internal sealed class DatabaseInitializer(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();

        // shortcut: EnsureCreated instead of migrations, switch to migrations before the schema must evolve in production
        await db.Database.EnsureCreatedAsync(cancellationToken);
        // WAL lets the API read while the dispatcher writes.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        await scope.ServiceProvider.GetRequiredService<EmployeeSeeder>().SeedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
