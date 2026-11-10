using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sms.Core.Application;
using Sms.Core.Domain;
using Sms.Infrastructure.Persistence;

namespace Sms.Api.Tests;

public sealed class PersistenceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PersistenceTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task MessageRepository_DuplicateIdempotencyKey_IsRejectedByDatabase()
    {
        await using var db = NewContext();
        var repository = new MessageRepository(db);
        await repository.AddAsync(NewMessage("key-1"), CancellationToken.None);

        await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() =>
            repository.AddAsync(NewMessage("key-1"), CancellationToken.None));

        // The failed insert was detached, so the context is still usable.
        await repository.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(1, await db.Messages.CountAsync());
    }

    [Fact]
    public async Task MessageRepository_StaleVersion_SaveFails()
    {
        var message = NewMessage(idempotencyKey: null);
        await using (var db = NewContext())
            await new MessageRepository(db).AddAsync(message, CancellationToken.None);

        await using var first = NewContext();
        await using var second = NewContext();
        var firstRepository = new MessageRepository(first);
        var secondRepository = new MessageRepository(second);
        var firstCopy = (await firstRepository.GetAsync(message.Id, CancellationToken.None))!;
        var secondCopy = (await secondRepository.GetAsync(message.Id, CancellationToken.None))!;
        var recipientId = message.Recipients[0].Id;

        firstCopy.LockRecipient(recipientId, Now.AddMinutes(1), Now);
        await firstRepository.SaveChangesAsync(CancellationToken.None);
        secondCopy.LockRecipient(recipientId, Now.AddMinutes(1), Now);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => secondRepository.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EmployeeSeed_DuplicateNameAndAddress_IsRejected()
    {
        await using var db = NewContext();
        db.Employees.Add(Employee.Create("Dana", "Levi", "Herzl 5 Rehovot"));
        await db.SaveChangesAsync();

        db.Employees.Add(Employee.Create(" dana ", "LEVI", "Herzl  5   Rehovot"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task EmployeePhones_SameNumberForTwoEmployees_IsAllowed()
    {
        await using var db = NewContext();
        var dana = Employee.Create("Dana", "Levi", "Herzl 5 Rehovot");
        var noa = Employee.Create("Noa", "Levi", "Herzl 5 Rehovot");
        dana.Phones.Add(new EmployeePhone("+972501234567"));
        noa.Phones.Add(new EmployeePhone("+972501234567"));
        db.Employees.AddRange(dana, noa);

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.EmployeePhones.CountAsync());
    }

    private SmsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SmsDbContext>().UseSqlite(_connection).Options);

    private static Message NewMessage(string? idempotencyKey)
    {
        MessageBody.TryCreate("Shift starts at 07:00", out var body, out _);
        PhoneNumber.TryCreate("050-1234567", out var phone);
        return Message.Create(body!, [phone!], idempotencyKey, Now);
    }
}
