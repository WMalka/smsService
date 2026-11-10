using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sms.Core.Domain;

namespace Sms.Infrastructure.Persistence;

internal sealed class SmsDbContext(DbContextOptions<SmsDbContext> options) : DbContext(options)
{
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageRecipient> MessageRecipients => Set<MessageRecipient>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeePhone> EmployeePhones => Set<EmployeePhone>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<MessageStatus>().HaveConversion<string>();
        configurationBuilder.Properties<RecipientStatus>().HaveConversion<string>();
        // SQLite returns DateTime with Kind=Unspecified; every time we store is UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Message>(message =>
        {
            message.Property(m => m.Id).ValueGeneratedNever();
            message.Property(m => m.Version).IsConcurrencyToken();
            message.HasIndex(m => m.IdempotencyKey).IsUnique();
            message.HasIndex(m => new { m.Status, m.CreatedAt, m.Id });
            message.HasIndex(m => new { m.CreatedAt, m.Id });
            message.HasMany(m => m.Recipients).WithOne().HasForeignKey(r => r.MessageId);
            message.Navigation(m => m.Recipients).HasField("_recipients").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<MessageRecipient>(recipient =>
        {
            recipient.Property(r => r.Id).ValueGeneratedNever();
            recipient.HasIndex(r => new { r.MessageId, r.PhoneNumber }).IsUnique();
            recipient.HasIndex(r => new { r.Status, r.NextActionAt });
        });

        modelBuilder.Entity<Employee>(employee =>
        {
            employee.Property(e => e.FirstName).UseCollation("NOCASE");
            employee.Property(e => e.LastName).UseCollation("NOCASE");
            employee.Property(e => e.Address).UseCollation("NOCASE");
            employee.HasIndex(e => new { e.FirstName, e.LastName, e.Address }).IsUnique();
            employee.HasMany(e => e.Phones).WithOne().HasForeignKey(p => p.EmployeeId);
        });

        modelBuilder.Entity<EmployeePhone>(phone =>
        {
            phone.HasIndex(p => new { p.EmployeeId, p.PhoneNumber }).IsUnique();
            phone.HasIndex(p => p.PhoneNumber);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
