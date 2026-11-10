using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sms.Core.Application;
using Sms.Core.Sending;
using Sms.Infrastructure.Sending;

namespace Sms.Api.Tests;

public sealed class LoggingTests
{
    [Fact]
    public async Task Logs_NeverContainBody_AndMaskPhoneNumbers()
    {
        const string body = "Secret shift plan for tomorrow";
        var logs = new CapturingLoggerProvider();
        var providerHttp = StubHttpHandler.Returning(HttpStatusCode.OK, """{"providerId":"p-1"}""");
        using var baseFactory = new SmsApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder
            .ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace))
            .ConfigureTestServices(services =>
            {
                // The real adapter (it is what logs phone numbers) over a stub HTTP handler.
                services.RemoveAll<ISmsProvider>();
                services.AddHttpClient<ISmsProvider, MockSmsProvider>().ConfigurePrimaryHttpMessageHandler(() => providerHttp);
            }));
        var client = factory.CreateClient();

        var posted = await client.PostAsJsonAsync("/messages", new { to = new[] { "050-1234567" }, body });
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DispatchPending>().ExecuteAsync(CancellationToken.None);

        Assert.Single(providerHttp.Requests);
        var all = string.Join('\n', logs.Lines);
        Assert.DoesNotContain(body, all);
        Assert.DoesNotContain("+972501234567", all);
        Assert.DoesNotContain("0501234567", all);
        Assert.DoesNotContain("050-1234567", all);
        Assert.Contains("+97250****567", all);
    }
}
