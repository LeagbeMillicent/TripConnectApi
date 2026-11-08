using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RiconRide.Api.Config;

/// <summary>
/// Enriches the generated OpenAPI contract (the source of the Postman
/// collection): document info, bearer auth, and a summary + description for
/// every operation, kept in one table so endpoint files stay readable.
/// </summary>
public static partial class OpenApiDocs
{
    private static readonly Dictionary<string, (string Summary, string Description)> Ops = new()
    {
        ["POST api/auth/login"] = ("Operator login", "Returns a bearer JWT and also sets the HttpOnly `rr_session` cookie (SameSite=Strict). 5 wrong passwords lock the account for 15 minutes (423)."),
        ["POST api/auth/logout"] = ("Operator logout", "Clears the session cookie. Bearer tokens simply expire."),
        ["GET api/auth/me"] = ("Current operator", "The signed-in operator's profile and role."),

        ["GET api/trips"] = ("Search scheduled trips", "Public. Upcoming scheduled/boarding trips; `passengers` hides trips without enough free seats. Cities match case-insensitively."),
        ["GET api/trips/{id}"] = ("Trip details and seat map", "Public. Seat map shows occupancy only, never who sits where."),
        ["POST api/trips"] = ("Schedule a departure", "CMS. `departureTime` like `06:30 AM`, `duration` like `4h 30m`, `basePrice` in GH₵ (max 2 decimals)."),
        ["PATCH api/trips/{id}"] = ("Update a departure", "CMS. Partial update. Price changes apply to new bookings only. `status`: scheduled | boarding | departed | arrived."),
        ["DELETE api/trips/{id}"] = ("Cancel or delete a departure", "CMS. With no bookings the trip is deleted (204). With bookings it is cancelled, paid tickets become `refund_due` and passengers are notified (200)."),
        ["POST api/trips/{id}/hold-seat"] = ("Hold seats during checkout", "Public. Locks seats for 10 minutes for `sessionId`. Re-posting replaces that session's holds. 409 lists `unavailableSeats`."),

        ["POST api/bookings"] = ("Create a ticket reservation", "Public. Creates a `pending_payment` booking and opens the payment; seats are released if unpaid after 15 minutes. Pass the `sessionId` used for hold-seat to convert held seats. `qrCodeData` stays null until paid."),
        ["POST api/bookings/find-ticket"] = ("Find my ticket by phone", "Public. Always 202 with the same message: boarding links go only to the phone by SMS/WhatsApp, so the response never reveals whether a number has tickets."),
        ["GET api/bookings/ticket/{bookingNumber}"] = ("Open a ticket from its link", "Public. The `token` from the SMS/WhatsApp link is required; wrong tokens get 404."),
        ["GET api/bookings"] = ("Passenger manifest", "CMS. Filter by `status` (pending_payment | confirmed | boarded | expired | cancelled | refund_due), `tripId`, `date`; `search` matches booking number, passenger name or phone. Paged (`limit` max 100)."),
        ["PATCH api/bookings/{id}/check-in"] = ("Board a passenger (QR scan)", "CMS. `{id}` is the booking number or id. Send the scanned `qrCodeData` (signature verified) or no body for manual entry. Only confirmed tickets for today's departures."),

        ["POST api/groups"] = ("Request a group charter", "Public. Created as `pending_approval`; dispatch sets the fare per person. Returns the invite code."),
        ["PATCH api/groups/{id}/fare-approval"] = ("Approve the charter fare", "CMS. Sets fare per person, coach and notes. `status`: planning | confirmed | cancelled. The fare is locked once anyone has joined."),
        ["GET api/groups/code/{code}"] = ("Look up a charter by invite code", "Public. Status, pickup map link, approved fare, confirmed members and seats remaining. No member or organiser contact details."),
        ["POST api/groups/{id}/join"] = ("Join a charter", "Public. Only after fare approval. Reserves a place for 15 minutes pending payment; one active place per phone."),

        ["GET api/vehicles"] = ("Rental vehicle catalogue", "Public. Filters: `category` (SUV | Bus | Sedan | Pickup | Van), `driverOption` (self-drive | with-driver), `availability=available` with optional `startDate`/`endDate`. Vehicles with lapsed DVLA roadworthiness are never listed."),
        ["POST api/vehicles"] = ("Register a vehicle", "CMS. Prices in GH₵. Images must be https URLs. Plate must be unique."),
        ["POST api/rentals"] = ("Create a rental agreement", "Public. Price = full weeks at the weekly rate + remaining days at the daily rate (capped at a week), + driver fee per day, + refundable deposit. Self-drive needs `driverLicenseNumber`."),
        ["PATCH api/rentals/{id}/deposit"] = ("Settle the security deposit", "CMS. `action`: release | deduct | forfeit. The refund goes back through the gateway to the original payment method. A deposit can only be settled once."),

        ["GET api/terminals"] = ("List terminals", "Public. Includes a Google Maps directions link and marshal contact."),
        ["POST api/terminals"] = ("Register a terminal", "CMS. `id` defaults to `st-<slug of name>`."),
        ["PATCH api/terminals/{id}"] = ("Update a terminal", "CMS. Partial update; inactive terminals can't be used for new trips."),

        ["GET api/operators"] = ("List transport operators", "Public. Active operators. `rating` is null until a real review system exists."),
        ["POST api/operators"] = ("Register a transport operator", "CMS. `id` defaults to `op-<slug of name>`. Rating is not writable."),
        ["PATCH api/operators/{id}"] = ("Update a transport operator", "CMS. Deactivating stops new trips under it; existing trips are unaffected. The id cannot change."),

        ["POST api/payments/initialize"] = ("Open (or retry) a payment", "Public. `referenceType`: ticket | charter | rental; `referenceId` is the booking number, charter member id or rental number. The amount comes from the order; a different `amount` is rejected (422)."),
        ["POST api/payments/webhook"] = ("Payment gateway webhook", "Paystack only. Verifies the HMAC-SHA512 `x-paystack-signature` over the raw body. Idempotent; a short or wrong-currency payment is flagged `refund_due`, never fulfilled."),
        ["GET api/payments/verify/{reference}"] = ("Check a payment", "Public. If still pending, asks the gateway directly (covers a missed webhook). Status: pending | paid | failed | refund_due."),
        ["POST api/payments/mock/{reference}/complete"] = ("DEV ONLY: settle a mock payment", "Exists only while no PAYSTACK_SECRET_KEY is set. Simulates the customer approving the MoMo prompt."),

        ["GET api/content/banners"] = ("Live advisories", "Public. Active banners inside their time window."),
        ["POST api/content/banners"] = ("Publish an advisory", "CMS. `severity`: info | warning | critical."),
        ["GET api/content/faqs"] = ("FAQs", "Public. Published FAQs, optional `category` filter."),
        ["POST api/content/faqs"] = ("Add an FAQ", "CMS."),
        ["GET api/admin/dashboard-stats"] = ("Operations dashboard KPIs", "CMS. Every figure is a count or sum of real rows: gross volume (all time, today), seats booked today, fleet in transit, pending fare approvals, payments awaiting refund."),
        ["GET health"] = ("Health check", "200 when the database is reachable."),
    };

    // Request bodies taken from the platform spec's own examples, so the
    // Postman collection opens with a body that actually validates.
    private static readonly Dictionary<string, string> Bodies = new()
    {
        ["POST api/auth/login"] = """{ "email": "admin@riconride.com", "password": "dev-admin-pass-2026" }""",
        ["POST api/trips"] = """{ "originCity": "Accra", "destinationCity": "Tamale", "originStationId": "st-circle", "destinationStationId": "st-tamale-central", "departureDate": "2026-12-12", "departureTime": "05:00 AM", "duration": "9h 30m", "basePrice": 240, "operatorId": "op-stc", "vehicleType": "Intercity Scania Touring (49-Seater)", "totalSeatsCount": 49, "amenities": ["Air Conditioning", "Luggage Hold"], "pickupLandmarks": ["Circle Overpass Bay 4"] }""",
        ["PATCH api/trips/{id}"] = """{ "departureTime": "06:00 AM", "status": "boarding" }""",
        ["POST api/trips/{id}/hold-seat"] = """{ "seatNumbers": [12, 13], "sessionId": "sess_891273" }""",
        ["POST api/bookings"] = """{ "tripId": "trip-acc-kum-01", "selectedSeatNumbers": [14, 15], "passengers": [ { "fullName": "Kwame Mensah", "phone": "0539943305", "gender": "male" }, { "fullName": "Akua Serwaa", "phone": "0241002000", "gender": "female" } ], "paymentMethod": "momo", "mobileMoneyNumber": "0539943305", "mobileMoneyProvider": "MTN", "sessionId": "sess_891273" }""",
        ["POST api/bookings/find-ticket"] = """{ "phone": "0539943305" }""",
        ["PATCH api/bookings/{id}/check-in"] = """{ "qrCodeData": "RC-2026-1234567|Accra-Kumasi|SEATS:14,15|Kwame Mensah|GH₵220|SIG:<from the ticket>" }""",
        ["POST api/groups"] = """{ "name": "KNUST Alumni Annual Homecoming Trip", "tripType": "Church / Alumni", "origin": "Accra (Circle Station)", "destination": "Kumasi (KNUST Campus)", "pickupLocation": "Legon Shell Landmark / University Gate", "pickupCoordinates": { "lat": 5.6508, "lng": -0.1870 }, "travelDate": "2026-12-25", "preferredDepartureTime": "06:00 AM", "targetTravelers": 30, "organizer": { "name": "Kofi Mensah Boateng", "phone": "0539943305", "email": "kofi@example.com" } }""",
        ["PATCH api/groups/{id}/fare-approval"] = """{ "approvedFarePerPerson": 145, "assignedCoach": "30-Seater Coaster Bus with AC", "dispatchNotes": "Assigned executive shuttle. Fuel allowance and driver per diem covered.", "status": "planning" }""",
        ["POST api/groups/{id}/join"] = """{ "name": "Ama Osei", "phone": "0539943305", "emergencyContact": "0241002000", "paymentMethod": "momo", "mobileMoneyProvider": "MTN" }""",
        ["POST api/vehicles"] = """{ "name": "Toyota Land Cruiser Prado TX", "category": "SUV", "licensePlate": "GW 8942-23", "dailyPrice": 950, "weeklyPrice": 5800, "securityDeposit": 1000, "withDriverAllowed": true, "selfDriveAllowed": true, "driverFeePerDay": 200, "features": ["4x4 Drive", "Leather Interior", "GPS Tracker", "Comprehensive Insurance"], "dvlaRoadworthinessExpiry": "2027-05-15", "images": [] }""",
        ["POST api/rentals"] = """{ "vehicleId": "<vehicle id from GET /api/vehicles>", "startDate": "2026-12-01", "endDate": "2026-12-08", "pickupPoint": "Kotoka International Airport, Terminal 3", "driverOption": "with-driver", "customer": { "name": "Yaw Asante", "phone": "0551234567", "email": "yaw@example.com" }, "paymentMethod": "momo", "mobileMoneyProvider": "MTN" }""",
        ["PATCH api/rentals/{id}/deposit"] = """{ "action": "deduct", "deductions": [ { "reason": "Fuel top-up", "amount": 150 } ], "inspectionNotes": "Returned with a quarter tank; no damage." }""",
        ["POST api/terminals"] = """{ "name": "Takoradi Market Circle Terminal", "city": "Takoradi", "address": "Market Circle", "latitude": 4.8975, "longitude": -1.7603, "activeBayCount": 6, "marshalName": "Esi Arthur", "marshalPhone": "0244000000" }""",
        ["PATCH api/terminals/{id}"] = """{ "activeBayCount": 14 }""",
        ["POST api/operators"] = """{ "name": "Metro Mass Transit", "isVerified": true }""",
        ["PATCH api/operators/{id}"] = """{ "isActive": false }""",
        ["POST api/payments/initialize"] = """{ "referenceType": "ticket", "referenceId": "RC-2026-1234567", "amount": 220, "currency": "GHS", "customerEmail": "customer@riconride.com", "customerPhone": "0539943305", "paymentNetwork": "MTN" }""",
        ["POST api/payments/webhook"] = """{ "event": "charge.success", "data": { "reference": "RCP_...", "amount": 22000, "currency": "GHS", "status": "success" } }""",
        ["POST api/content/banners"] = """{ "title": "N1 diversion at Kasoa", "message": "Expect 30-minute delays on Accra–Cape Coast departures today.", "severity": "warning" }""",
        ["POST api/content/faqs"] = """{ "question": "Can I pay with Telecel Cash?", "answer": "Yes. MTN MoMo, Telecel Cash, AT Money, Visa and Mastercard are accepted.", "category": "Payments" }""",
    };

    // Example values for query/path parameters, by "path prefix|name" first, then by name.
    private static readonly Dictionary<string, string> Params = new()
    {
        ["api/trips|id"] = "trip-acc-kum-01",
        ["api/bookings|id"] = "RC-2026-1234567",
        ["api/terminals|id"] = "st-circle",
        ["api/operators|id"] = "op-vip",
        ["api/groups|id"] = "<charter id from POST /api/groups>",
        ["api/rentals|id"] = "<rental id from POST /api/rentals>",
        ["api/vehicles|category"] = "SUV", ["api/content|category"] = "Payments",
        ["search"] = "Kwame", ["tripId"] = "trip-acc-kum-01",
        ["from"] = "Accra", ["to"] = "Kumasi", ["date"] = "2026-12-12", ["passengers"] = "2", ["operator"] = "op-vip",
        ["bookingNumber"] = "RC-2026-1234567", ["token"] = "<token from the SMS link>", ["code"] = "ACC-KUM-7QX2",
        ["reference"] = "RCP_5e7a724bfb5f413d95533cbfe742c7c9", ["driverOption"] = "with-driver",
        ["availability"] = "available", ["startDate"] = "2026-12-01", ["endDate"] = "2026-12-08", ["city"] = "Accra",
        ["status"] = "confirmed", ["page"] = "1", ["limit"] = "25",
    };

    [GeneratedRegex(@"\{(\w+):[^}]+\}")]
    private static partial Regex RouteConstraint();

    /// <summary>"POST api/groups/{id:guid}/join" and "GET api/bookings/" → the table's key form.</summary>
    private static string Key(string? method, string? path) =>
        $"{method} {RouteConstraint().Replace(path ?? "", "{$1}").TrimEnd('/')}";

    public static void AddRiconRideOpenApi(this IServiceCollection services) => services.AddOpenApi(o =>
    {
        o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new OpenApiInfo
            {
                Title = "RiconRide API",
                Version = "v1",
                Description = "Every Journey, Connected. Intercity bus booking, group charters and vehicle rentals in Ghana. " +
                              "Money is in Ghana Cedi (GH₵, max 2 decimals); phone numbers are Ghana mobiles (0XXXXXXXXX or +233…). " +
                              "Errors are RFC 7807 problem+json; validation errors list fields under `errors`.",
            };
            doc.Servers = [new OpenApiServer { Url = "http://localhost:5080", Description = "Local development" }];
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "Operator token from POST /api/auth/login.",
            };
            return Task.CompletedTask;
        });

        o.AddOperationTransformer((op, ctx, _) =>
        {
            var d = ctx.Description;
            var key = Key(d.HttpMethod, d.RelativePath);
            if (Ops.TryGetValue(key, out var text))
            {
                op.Summary = text.Summary;
                op.Description = text.Description;
            }
            if (Bodies.TryGetValue(key, out var body) && op.RequestBody?.Content is { } content)
                foreach (var media in content.Values) media.Example = JsonNode.Parse(body);
            var prefix = string.Join('/', (d.RelativePath ?? "").Split('/').Take(2));
            foreach (var p in op.Parameters?.OfType<OpenApiParameter>() ?? [])
                if (p.Name is not null && (Params.TryGetValue($"{prefix}|{p.Name}", out var ex) || Params.TryGetValue(p.Name, out ex)))
                    // Integer params need a numeric example, or generators fall back to 0.
                    p.Example = p.Schema?.Type?.HasFlag(JsonSchemaType.Integer) == true && long.TryParse(ex, out var n)
                        ? JsonValue.Create(n) : JsonValue.Create(ex);
            var auth = d.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().ToList();
            var policyRoles = d.ActionDescriptor.EndpointMetadata.OfType<AuthorizationPolicy>()
                .SelectMany(p => p.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>())
                .SelectMany(r => r.AllowedRoles).Distinct().ToList();
            if (auth.Count > 0 || policyRoles.Count > 0)
            {
                op.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", ctx.Document)] = [] }];
                if (policyRoles.Count > 0) op.Description = $"{op.Description}\n\n**Roles:** {string.Join(", ", policyRoles)}.";
            }
            return Task.CompletedTask;
        });
    });
}
