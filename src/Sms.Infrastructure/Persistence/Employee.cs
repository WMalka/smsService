using System.Text.RegularExpressions;

namespace Sms.Infrastructure.Persistence;

/// <summary>Seeded reference data (A5). Identified by FirstName + LastName + Address, case-insensitive (D20).</summary>
internal sealed class Employee
{
    public int Id { get; private set; }
    public string FirstName { get; private set; } = "";
    public string LastName { get; private set; } = "";
    public string Address { get; private set; } = "";
    public List<EmployeePhone> Phones { get; } = [];

    private Employee() { } // EF Core

    /// <summary>Normalizes the identity so the unique index sees "Dana  Levi " and "dana levi" as the same person.</summary>
    public static Employee Create(string firstName, string lastName, string address) => new()
    {
        FirstName = Normalize(firstName),
        LastName = Normalize(lastName),
        Address = Normalize(address),
    };

    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
}

internal sealed class EmployeePhone
{
    public int Id { get; private set; }
    public int EmployeeId { get; private set; }
    /// <summary>E.164. Not unique: two employees may share a number.</summary>
    public string PhoneNumber { get; private set; } = "";

    private EmployeePhone() { } // EF Core

    public EmployeePhone(string e164PhoneNumber) => PhoneNumber = e164PhoneNumber;
}
