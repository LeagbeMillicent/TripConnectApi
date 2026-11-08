using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RiconRide.Api.Config;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;

namespace RiconRide.Api.Auth;

public static class Roles
{
    public static string Wire(OperatorRole r) => r switch
    {
        OperatorRole.SystemAdmin => "system_admin",
        OperatorRole.DispatchMarshal => "dispatch_marshal",
        OperatorRole.StationMaster => "station_master",
        OperatorRole.GateMarshal => "gate_marshal",
        OperatorRole.FleetManager => "fleet_manager",
        OperatorRole.NetworkManager => "network_manager",
        OperatorRole.Communications => "communications",
        OperatorRole.Executive => "executive",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public static readonly IReadOnlyDictionary<string, OperatorRole> ByWire =
        Enum.GetValues<OperatorRole>().ToDictionary(Wire, r => r, StringComparer.OrdinalIgnoreCase);

    /// <summary>Restricts a route to the given roles. System admins are always allowed.</summary>
    public static TBuilder RequireRoles<TBuilder>(this TBuilder b, params OperatorRole[] roles) where TBuilder : IEndpointConventionBuilder =>
        b.RequireAuthorization(p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireRole(roles.Append(OperatorRole.SystemAdmin).Distinct().Select(Wire)));

    public static Guid? OperatorId(this ClaimsPrincipal u) =>
        Guid.TryParse(u.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? u.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public static class AuthSetup
{
    public static void AddOperatorAuth(this IServiceCollection services, JwtOptions jwt)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                RoleClaimType = "role",
                NameClaimType = JwtRegisteredClaimNames.Sub,
                ClockSkew = TimeSpan.FromMinutes(1),
            };
            // The CMS may use the HttpOnly cookie instead of a bearer header.
            // The cookie is SameSite=Strict, so cross-site pages cannot ride it (CSRF).
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (string.IsNullOrEmpty(ctx.Token) && ctx.Request.Cookies.TryGetValue(jwt.CookieName, out var c)) ctx.Token = c;
                    return Task.CompletedTask;
                },
            };
        });
        services.AddAuthorization();
    }

    public static string IssueToken(OperatorUser u, JwtOptions jwt, DateTimeOffset expires)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, u.Email),
            new Claim("name", u.FullName),
            new Claim("role", Roles.Wire(u.Role)),
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims, expires: expires.UtcDateTime, signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class AuthEndpoints
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    private static readonly PasswordHasher<OperatorUser> Hasher = new();

    public sealed record LoginRequest(string? Email, string? Password);

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest req, AppDbContext db, IOptions<JwtOptions> jwtOpt, TimeProvider clock, HttpContext http) =>
        {
            var v = new Validator().Require("email", req.Email, 254).Require("password", req.Password, 200);
            if (!v.IsValid) return v.Problem();

            var jwt = jwtOpt.Value;
            var now = clock.GetUtcNow();
            var email = req.Email!.Trim().ToLowerInvariant();
            var user = await db.OperatorUsers.SingleOrDefaultAsync(u => u.Email == email);
            // One generic message for unknown email, wrong password and disabled
            // account, so the endpoint cannot be used to discover staff emails.
            var denied = Results.Problem(statusCode: 401, title: "Invalid email or password.");
            if (user is null || !user.IsActive) return denied;
            if (user.LockedUntil > now)
                return Results.Problem(statusCode: 423, title: "Account temporarily locked after repeated failed logins. Try again later.");

            if (Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password!) == PasswordVerificationResult.Failed)
            {
                user.FailedLogins++;
                if (user.FailedLogins >= MaxFailures) { user.LockedUntil = now + LockFor; user.FailedLogins = 0; }
                await db.SaveChangesAsync();
                return denied;
            }

            user.FailedLogins = 0;
            user.LockedUntil = null;
            user.LastLoginAt = now;
            await db.SaveChangesAsync();

            var expires = now.AddHours(jwt.LifetimeHours);
            var token = AuthSetup.IssueToken(user, jwt, expires);
            http.Response.Cookies.Append(jwt.CookieName, token, new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Expires = expires, Path = "/",
            });
            return Results.Ok(new { token, expiresAt = expires, user = Present(user) });
        }).RequireRateLimiting("auth");

        g.MapPost("/logout", (IOptions<JwtOptions> jwt, HttpContext http) =>
        {
            http.Response.Cookies.Delete(jwt.Value.CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        });

        g.MapGet("/me", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var id = principal.OperatorId();
            var user = id is null ? null : await db.OperatorUsers.FindAsync(id.Value);
            return user is { IsActive: true } ? Results.Ok(Present(user)) : Results.Unauthorized();
        }).RequireRoles(Enum.GetValues<OperatorRole>());
    }

    private static object Present(OperatorUser u) => new { id = u.Id, email = u.Email, fullName = u.FullName, role = Roles.Wire(u.Role) };

    public static string Hash(OperatorUser u, string password) => Hasher.HashPassword(u, password);
}
