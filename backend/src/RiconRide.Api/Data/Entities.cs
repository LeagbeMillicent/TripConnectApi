namespace RiconRide.Api.Data;

// Money is stored as integer pesewas (1 GH₵ = 100 pesewas) everywhere in the
// database so no rounding happens in storage or sums. Conversion to the
// GH₵ numbers the API contract shows happens only in Domain/Money.cs.

public enum OperatorRole
{
    SystemAdmin, DispatchMarshal, StationMaster, GateMarshal,
    FleetManager, NetworkManager, Communications, Executive,
}

public enum TripStatus { Scheduled, Boarding, Departed, Arrived, Cancelled }

public enum BookingStatus { PendingPayment, Confirmed, Boarded, Expired, Cancelled, RefundDue }

public enum GroupStatus { PendingApproval, Planning, Confirmed, Cancelled }

public enum MemberStatus { PendingPayment, Paid, Expired }

public enum RentalStatus { PendingPayment, Confirmed, Completed, Cancelled, Expired }

public enum DepositStatus { NotHeld, Held, Released, PartiallyReleased, Forfeited }

public enum PaymentKind { Ticket, Charter, Rental }

public enum PaymentStatus { Pending, Success, Failed, RefundDue }

public class OperatorUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string FullName { get; set; }
    public required string PasswordHash { get; set; }
    public OperatorRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A transport company (VIP, STC, ...). Rating is null until a real review system exists.</summary>
public class TransportOperator
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public decimal? Rating { get; set; }
    public bool IsVerified { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Terminal
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string City { get; set; }
    public required string Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int ActiveBayCount { get; set; }
    public string? MarshalName { get; set; }
    public string? MarshalPhone { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Trip
{
    public required string Id { get; set; }
    public required string OriginCity { get; set; }
    public required string DestinationCity { get; set; }
    public required string OriginTerminalId { get; set; }
    public Terminal OriginTerminal { get; set; } = null!;
    public required string DestinationTerminalId { get; set; }
    public Terminal DestinationTerminal { get; set; } = null!;
    public DateOnly DepartureDate { get; set; }
    public TimeOnly DepartureTime { get; set; }
    public int DurationMinutes { get; set; }
    public long BasePricePesewas { get; set; }
    public required string OperatorId { get; set; }
    public TransportOperator Operator { get; set; } = null!;
    public required string VehicleType { get; set; }
    public int TotalSeats { get; set; }
    public List<string> Amenities { get; set; } = [];
    public List<string> PickupLandmarks { get; set; } = [];
    public TripStatus Status { get; set; } = TripStatus.Scheduled;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One row per taken seat — either a checkout hold or a booking.
/// The unique (TripId, SeatNumber) index is what makes double booking
/// impossible, even under concurrent requests. Rows with ExpiresAt in the
/// past are dead and purged before any seat is read or taken.
/// </summary>
public class SeatReservation
{
    public long Id { get; set; }
    public required string TripId { get; set; }
    public int SeatNumber { get; set; }
    public Guid? BookingId { get; set; }
    public string? HoldSessionId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string BookingNumber { get; set; }
    public required string TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public List<BookingPassenger> Passengers { get; set; } = [];
    public required string ContactPhone { get; set; }
    public long TotalPesewas { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PendingPayment;
    public required string PaymentMethod { get; set; }
    public string? MobileMoneyNumber { get; set; }
    public string? MobileMoneyProvider { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
    public Guid? CheckedInBy { get; set; }
}

public class BookingPassenger
{
    public long Id { get; set; }
    public Guid BookingId { get; set; }
    public required string FullName { get; set; }
    public required string Phone { get; set; }
    public string? Gender { get; set; }
    public int SeatNumber { get; set; }
}

public class GroupCharter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string InviteCode { get; set; }
    public required string Name { get; set; }
    public required string TripType { get; set; }
    public required string Origin { get; set; }
    public required string Destination { get; set; }
    public required string PickupLocation { get; set; }
    public double? PickupLatitude { get; set; }
    public double? PickupLongitude { get; set; }
    public DateOnly TravelDate { get; set; }
    public TimeOnly PreferredDepartureTime { get; set; }
    public int TargetTravelers { get; set; }
    public required string OrganizerName { get; set; }
    public required string OrganizerPhone { get; set; }
    public string? OrganizerEmail { get; set; }
    public GroupStatus Status { get; set; } = GroupStatus.PendingApproval;
    public long? ApprovedFarePesewas { get; set; }
    public string? AssignedCoach { get; set; }
    public string? DispatchNotes { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<GroupMember> Members { get; set; } = [];
}

public class GroupMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public required string Name { get; set; }
    public required string Phone { get; set; }
    public string? EmergencyContact { get; set; }
    public required string PaymentMethod { get; set; }
    public MemberStatus Status { get; set; } = MemberStatus.PendingPayment;
    public long FarePesewas { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public class Vehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Category { get; set; }
    public required string LicensePlate { get; set; }
    public long DailyPesewas { get; set; }
    public long? WeeklyPesewas { get; set; }
    public long DepositPesewas { get; set; }
    public bool WithDriverAllowed { get; set; }
    public bool SelfDriveAllowed { get; set; }
    public long DriverFeePerDayPesewas { get; set; }
    public List<string> Features { get; set; } = [];
    public DateOnly DvlaRoadworthinessExpiry { get; set; }
    public List<string> Images { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Rental
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string RentalNumber { get; set; }
    public Guid VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public required string PickupPoint { get; set; }
    public bool WithDriver { get; set; }
    public required string CustomerName { get; set; }
    public required string CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }
    public string? DriverLicenseNumber { get; set; }
    public long RentalPesewas { get; set; }
    public long DriverFeePesewas { get; set; }
    public long DepositPesewas { get; set; }
    public long TotalPesewas { get; set; }
    public RentalStatus Status { get; set; } = RentalStatus.PendingPayment;
    public DepositStatus DepositStatus { get; set; } = DepositStatus.NotHeld;
    public List<RentalDeduction> Deductions { get; set; } = [];
    public long? DepositRefundPesewas { get; set; }
    public string? InspectionNotes { get; set; }
    public DateTimeOffset? DepositSettledAt { get; set; }
    public Guid? DepositSettledBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public class RentalDeduction
{
    public long Id { get; set; }
    public Guid RentalId { get; set; }
    public required string Reason { get; set; }
    public long AmountPesewas { get; set; }
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Reference { get; set; }
    public PaymentKind Kind { get; set; }
    public Guid SubjectId { get; set; }
    public long AmountPesewas { get; set; }
    public string Currency { get; set; } = "GHS";
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public required string Provider { get; set; }
    public required string CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }
    public string? Network { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
}

public class Banner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public required string Message { get; set; }
    public string Severity { get; set; } = "info";
    public string? LinkUrl { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Faq
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Question { get; set; }
    public required string Answer { get; set; }
    public string? Category { get; set; }
    public int SortOrder { get; set; }
    public bool IsPublished { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
