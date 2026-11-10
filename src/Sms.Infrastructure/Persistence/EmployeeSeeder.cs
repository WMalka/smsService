using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sms.Core.Domain;

namespace Sms.Infrastructure.Persistence;

/// <summary>Fills the employee tables from a JSON file when they are empty (A5).</summary>
internal sealed class EmployeeSeeder(
    SmsDbContext db, IConfiguration configuration, IHostEnvironment environment, ILogger<EmployeeSeeder> logger)
{
    public const string FileSetting = "Seed:EmployeesFile";
    private const string DefaultFile = "seed/employees.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record SeedEmployee(string FirstName, string LastName, string Address, string[] Phones);

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Employees.AnyAsync(cancellationToken))
            return;

        var path = Path.Combine(environment.ContentRootPath, configuration[FileSetting] ?? DefaultFile);
        if (!File.Exists(path))
        {
            logger.LogInformation("Employee seed file {SeedFile} not found, skipping seeding", path);
            return;
        }

        await using var file = File.OpenRead(path);
        var seed = await JsonSerializer.DeserializeAsync<SeedEmployee[]>(file, JsonOptions, cancellationToken) ?? [];
        db.Employees.AddRange(seed.Select(ToEmployee));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {EmployeeCount} employees", seed.Length);
    }

    private Employee ToEmployee(SeedEmployee seed)
    {
        var employee = Employee.Create(seed.FirstName, seed.LastName, seed.Address);
        foreach (var raw in seed.Phones)
        {
            if (PhoneNumber.TryCreate(raw, out var phone))
                employee.Phones.Add(new EmployeePhone(phone!.Value));
            else
                logger.LogWarning("Skipping an invalid phone number in the employee seed file");
        }
        return employee;
    }
}
