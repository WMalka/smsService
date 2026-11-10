using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Sms.Core.Sending;
using Sms.Infrastructure;
using Sms.Infrastructure.Persistence;

namespace Sms.Api.Tests;

/// <summary>The real API over a private in-memory SQLite database, a fake provider, a fake clock and no background worker.</summary>
public sealed class SmsApiFactory : WebApplicationFactory<Program>
{
    // An in-memory SQLite database lives as long as its connection is open.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    internal FakeSmsProvider Provider { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));

    public SmsApiFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<SmsDbContext>>();
            services.AddDbContext<SmsDbContext>(options => options.UseSqlite(_connection));

            services.Remove(services.Single(service => service.ImplementationType == typeof(DispatcherWorker)));

            services.RemoveAll<ISmsProvider>();
            services.AddSingleton<ISmsProvider>(Provider);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _connection.Dispose();
    }
}
