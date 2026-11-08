using Microsoft.EntityFrameworkCore;
using RiconRide.Api.Auth;
using RiconRide.Api.Config;
using RiconRide.Api.Domain;

namespace RiconRide.Api.Data;

public static class Seeder
{
    /// <summary>
    /// Operator (staff) accounts cannot be created over HTTP, so they come from
    /// config: Auth:SeedOperators:N:{Email,Password,FullName,Role}. Existing
    /// emails are left untouched (a changed password in config is NOT applied).
    /// Boot fails loudly on a bad entry rather than silently skipping it.
    /// </summary>
    public static async Task SeedOperatorsAsync(AppDbContext db, IEnumerable<SeedOperatorAccount> accounts)
    {
        var list = accounts.ToList();
        var bad = list.Where(a => string.IsNullOrWhiteSpace(a.Email) || a.Password.Length < 10 || !Roles.ByWire.ContainsKey(a.Role))
            .Select(a => a.Email).ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException($"Invalid Auth:SeedOperators entries (need email, password >= 10 chars, known role): {string.Join(", ", bad)}");

        foreach (var a in list)
        {
            var email = a.Email.Trim().ToLowerInvariant();
            if (await db.OperatorUsers.AnyAsync(u => u.Email == email)) continue;
            var user = new OperatorUser { Email = email, FullName = a.FullName, PasswordHash = "", Role = Roles.ByWire[a.Role] };
            user.PasswordHash = AuthEndpoints.Hash(user, a.Password);
            db.OperatorUsers.Add(user);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Development reference data so the website has something to show.
    /// Operator ratings are left null: no review system exists to back a number.
    /// </summary>
    public static async Task SeedDevelopmentAsync(AppDbContext db, TimeProvider clock)
    {
        if (await db.Terminals.AnyAsync()) return;

        db.TransportOperators.AddRange(
            new TransportOperator { Id = "op-vip", Name = "VIP Jeoun Transport", IsVerified = true },
            new TransportOperator { Id = "op-stc", Name = "STC Intercity Coaches", IsVerified = true });

        db.Terminals.AddRange(
            new Terminal { Id = "st-circle", Name = "VIP Jeoun Central Terminal, Circle", City = "Accra", Address = "Ring Road Central, Kwame Nkrumah Circle", Latitude = 5.5717, Longitude = -0.2153, ActiveBayCount = 12 },
            new Terminal { Id = "st-asafo", Name = "VIP Asafo Terminal", City = "Kumasi", Address = "Asafo Interchange", Latitude = 6.6880, Longitude = -1.6170, ActiveBayCount = 8 },
            new Terminal { Id = "st-tamale-central", Name = "Tamale Central Station", City = "Tamale", Address = "Central Business District", ActiveBayCount = 6 });

        var day = GhanaClock.Today(clock).AddDays(3);
        db.Trips.AddRange(
            new Trip
            {
                Id = "trip-acc-kum-01", OriginCity = "Accra", DestinationCity = "Kumasi", OriginTerminalId = "st-circle",
                DestinationTerminalId = "st-asafo", DepartureDate = day, DepartureTime = new TimeOnly(6, 30), DurationMinutes = 270,
                BasePricePesewas = 110_00, OperatorId = "op-vip", VehicleType = "Executive VIP Coach (49-Seater)", TotalSeats = 49,
                Amenities = ["Air Conditioning", "USB Charging", "Luggage Hold"], PickupLandmarks = ["Circle Overpass Bay 4"],
            },
            new Trip
            {
                Id = "trip-acc-tam-01", OriginCity = "Accra", DestinationCity = "Tamale", OriginTerminalId = "st-circle",
                DestinationTerminalId = "st-tamale-central", DepartureDate = day, DepartureTime = new TimeOnly(5, 0), DurationMinutes = 570,
                BasePricePesewas = 240_00, OperatorId = "op-stc", VehicleType = "Intercity Scania Touring (49-Seater)", TotalSeats = 49,
                Amenities = ["Air Conditioning", "Luggage Hold"],
            });

        db.Vehicles.Add(new Vehicle
        {
            Name = "Toyota Land Cruiser Prado TX", Category = "SUV", LicensePlate = "GW 8942-23", DailyPesewas = 950_00,
            WeeklyPesewas = 5800_00, DepositPesewas = 1000_00, WithDriverAllowed = true, SelfDriveAllowed = true,
            DriverFeePerDayPesewas = 200_00, Features = ["4x4 Drive", "Leather Interior", "GPS Tracker", "Comprehensive Insurance"],
            DvlaRoadworthinessExpiry = GhanaClock.Today(clock).AddYears(1),
        });

        db.Faqs.Add(new Faq
        {
            Question = "Do I need an account to book?",
            Answer = "No. Book with your phone number; use \"Find my ticket\" to have your boarding pass sent to that number.",
            Category = "Booking",
        });
        await db.SaveChangesAsync();
    }
}
