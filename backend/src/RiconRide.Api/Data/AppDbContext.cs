using Microsoft.EntityFrameworkCore;

namespace RiconRide.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<OperatorUser> OperatorUsers => Set<OperatorUser>();
    public DbSet<TransportOperator> TransportOperators => Set<TransportOperator>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<SeatReservation> SeatReservations => Set<SeatReservation>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingPassenger> BookingPassengers => Set<BookingPassenger>();
    public DbSet<GroupCharter> Groups => Set<GroupCharter>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Rental> Rentals => Set<Rental>();
    public DbSet<RentalDeduction> RentalDeductions => Set<RentalDeduction>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Banner> Banners => Set<Banner>();
    public DbSet<Faq> Faqs => Set<Faq>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Enums are stored as text so the database stays readable and adding
        // a member in the middle of an enum can never renumber existing rows.
        b.Properties<OperatorRole>().HaveConversion<string>();
        b.Properties<TripStatus>().HaveConversion<string>();
        b.Properties<BookingStatus>().HaveConversion<string>();
        b.Properties<GroupStatus>().HaveConversion<string>();
        b.Properties<MemberStatus>().HaveConversion<string>();
        b.Properties<RentalStatus>().HaveConversion<string>();
        b.Properties<DepositStatus>().HaveConversion<string>();
        b.Properties<PaymentKind>().HaveConversion<string>();
        b.Properties<PaymentStatus>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<OperatorUser>().HasIndex(x => x.Email).IsUnique();

        m.Entity<Trip>(e =>
        {
            e.HasOne(x => x.OriginTerminal).WithMany().HasForeignKey(x => x.OriginTerminalId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DestinationTerminal).WithMany().HasForeignKey(x => x.DestinationTerminalId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Operator).WithMany().HasForeignKey(x => x.OperatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OriginCity, x.DestinationCity, x.DepartureDate });
        });

        m.Entity<SeatReservation>(e =>
        {
            e.HasIndex(x => new { x.TripId, x.SeatNumber }).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Booking>(e =>
        {
            e.HasIndex(x => x.BookingNumber).IsUnique();
            e.HasIndex(x => x.ContactPhone);
            e.HasMany(x => x.Passengers).WithOne().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Trip).WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<BookingPassenger>().HasIndex(x => x.Phone);

        m.Entity<GroupCharter>(e =>
        {
            e.ToTable("group_charters");
            e.HasIndex(x => x.InviteCode).IsUnique();
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Vehicle>().HasIndex(x => x.LicensePlate).IsUnique();

        m.Entity<Rental>(e =>
        {
            e.HasIndex(x => x.RentalNumber).IsUnique();
            e.HasIndex(x => new { x.VehicleId, x.StartDate, x.EndDate });
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Deductions).WithOne().HasForeignKey(x => x.RentalId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Payment>(e =>
        {
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => new { x.Kind, x.SubjectId });
        });

        m.Entity<TransportOperator>().Property(x => x.Rating).HasPrecision(2, 1);
    }
}
