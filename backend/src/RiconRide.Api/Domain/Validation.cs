using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace RiconRide.Api.Domain;

/// <summary>Collects field errors and turns them into an RFC 7807 validation problem.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public bool IsValid => _errors.Count == 0;

    public Validator Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
        return this;
    }

    public Validator Require(string field, string? value, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(field, "is required.");
        else if (value.Length > maxLength) Add(field, $"must be at most {maxLength} characters.");
        return this;
    }

    public Validator Optional(string field, string? value, int maxLength = 200)
    {
        if (value is not null && value.Length > maxLength) Add(field, $"must be at most {maxLength} characters.");
        return this;
    }

    public string Phone(string field, string? value, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) Add(field, "is required.");
            return "";
        }
        if (!GhanaPhone.TryNormalize(value, out var e164)) Add(field, "must be a Ghana mobile number, e.g. 0539943305.");
        return e164;
    }

    public long Amount(string field, decimal? value, bool required = true, bool allowZero = false)
    {
        if (value is null)
        {
            if (required) Add(field, "is required.");
            return 0;
        }
        if (!Money.IsValidAmount(value.Value, allowZero))
        {
            Add(field, allowZero ? "must be a GH₵ amount of zero or more with at most 2 decimal places."
                                 : "must be a positive GH₵ amount with at most 2 decimal places.");
            return 0;
        }
        return Money.ToPesewas(value.Value);
    }

    public DateOnly Date(string field, string? value)
    {
        if (!Formats.TryParseDate(value, out var d)) Add(field, "must be a date in YYYY-MM-DD format.");
        return d;
    }

    public TimeOnly Time(string field, string? value)
    {
        if (!Formats.TryParseTime(value, out var t)) Add(field, "must be a time like 06:30 AM.");
        return t;
    }

    public T OneOf<T>(string field, string? value, IReadOnlyDictionary<string, T> allowed, bool required = true, T fallback = default!)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) Add(field, "is required.");
            return fallback;
        }
        if (allowed.TryGetValue(value.Trim(), out var v)) return v;
        Add(field, $"must be one of: {string.Join(", ", allowed.Keys)}.");
        return fallback;
    }

    /// <summary>Required choice among strings; returns the canonical spelling ("" when invalid).</summary>
    public string Choice(string field, string? value, IReadOnlyDictionary<string, string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) { Add(field, "is required."); return ""; }
        return OptionalChoice(field, value, allowed) ?? "";
    }

    /// <summary>Optional choice among strings: null when absent, canonical spelling when valid.</summary>
    public string? OptionalChoice(string field, string? value, IReadOnlyDictionary<string, string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (allowed.TryGetValue(value.Trim(), out var v)) return v;
        Add(field, $"must be one of: {string.Join(", ", allowed.Keys)}.");
        return null;
    }

    public IResult Problem() =>
        Results.ValidationProblem(_errors.ToDictionary(k => k.Key, v => v.Value.ToArray()));
}

public static class Problems
{
    public static IResult NotFound(string what) =>
        Results.Problem(statusCode: 404, title: $"{what} not found.");

    public static IResult Conflict(string title, object? extensions = null) =>
        Results.Problem(statusCode: 409, title: title,
            extensions: extensions is null ? null : new Dictionary<string, object?> { ["details"] = extensions });

    public static IResult Unprocessable(string title) => Results.Problem(statusCode: 422, title: title);

    public static IResult BadGateway(string title) => Results.Problem(statusCode: 502, title: title);

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

public static class Choices
{
    private static Dictionary<string, string> Ci(params string[] v) =>
        v.ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, string> PaymentMethods = Ci("momo", "card");
    public static readonly IReadOnlyDictionary<string, string> MomoProviders = Ci("MTN", "Telecel", "AT");
    public static readonly IReadOnlyDictionary<string, string> Genders = Ci("male", "female", "other");
    public static readonly IReadOnlyDictionary<string, string> VehicleCategories = Ci("SUV", "Bus", "Sedan", "Pickup", "Van");
    public static readonly IReadOnlyDictionary<string, string> BannerSeverities = Ci("info", "warning", "critical");
}
