using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sms.Core.Application;
using Sms.Core.Sending;
using Sms.Infrastructure.Persistence;
using Sms.Infrastructure.Sending;

namespace Sms.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SmsDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("Sms") ?? "Data Source=sms.db"));
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<EmployeeSeeder>();

        services.AddSingleton(configuration.GetSection(DispatcherOptions.Section).Get<DispatcherOptions>() ?? new DispatcherOptions());
        services.AddSingleton(TimeProvider.System);

        services.Configure<SmsProviderOptions>(configuration.GetSection(SmsProviderOptions.Section));
        services.AddHttpClient<ISmsProvider, MockSmsProvider>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<SmsProviderOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddScoped<SendMessage>();
        services.AddScoped<GetMessage>();
        services.AddScoped<ListMessages>();
        services.AddScoped<DispatchPending>();

        // Order matters: the database must exist before the dispatcher's first tick.
        services.AddHostedService<DatabaseInitializer>();
        services.AddHostedService<DispatcherWorker>();

        services.AddHealthChecks().AddDbContextCheck<SmsDbContext>();
        return services;
    }
}
