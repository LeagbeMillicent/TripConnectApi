using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RiconRide.Api.Domain;

/// <summary>The single place where GH₵ (wire format) and pesewas (storage) meet.</summary>
public static class Money
{
    public static long ToPesewas(decimal cedis) => (long)(cedis * 100m);

    public static decimal ToCedis(long pesewas) => pesewas / 100m;

    /// <summary>
    /// A valid GH₵ amount has at most two decimal places. Accepting 10.005
    /// and truncating would silently charge a different amount than was sent.
    /// </summary>
    public static bool IsValidAmount(decimal cedis, bool allowZero = false) =>
        (allowZero ? cedis >= 0 : cedis > 0) && cedis <= 10_000_000m && decimal.Round(cedis, 2) == cedis;

    public static string Format(long pesewas) =>
        "GH₵" + ToCedis(pesewas).ToString(pesewas % 100 == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);
}

public static partial class GhanaPhone
{
    // Ghana mobile numbers: 0XX XXX XXXX locally, +233 XX XXX XXXX internationally.
    [GeneratedRegex(@"^(?:\+?233|0)([235]\d{8})$")]
    private static partial Regex Pattern();

    /// <summary>Normalises to E.164 (+233…) so the same phone always matches in lookups.</summary>
    public static bool TryNormalize(string? raw, out string e164)
    {
        e164 = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var compact = Regex.Replace(raw, @"[\s\-()]", "");
        var m = Pattern().Match(compact);
        if (!m.Success) return false;
        e164 = "+233" + m.Groups[1].Value;
        return true;
    }

    public static string ToLocal(string e164) => e164.StartsWith("+233") ? "0" + e164[4..] : e164;

    /// <summary>Masked form for logs — numbers are personal data.</summary>
    public static string Mask(string e164) => e164.Length > 6 ? e164[..4] + "*****" + e164[^3..] : "***";
}

public static class Codes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I: read aloud over the phone

    public static string Digits(int n)
    {
        Span<char> c = stackalloc char[n];
        for (var i = 0; i < n; i++) c[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(c);
    }

    public static string Alnum(int n)
    {
        Span<char> c = stackalloc char[n];
        for (var i = 0; i < n; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(c);
    }

    // 7 random digits: the spec's 5-digit form (RC-2026-98124) only allows
    // 100k bookings a year before collisions dominate.
    public static string BookingNumber(DateTimeOffset now) => $"RC-{now.Year}-{Digits(7)}";

    public static string RentalNumber(DateTimeOffset now) => $"RR-{now.Year}-{Digits(7)}";

    public static string PaymentReference() => "RCP_" + Guid.NewGuid().ToString("N");

    public static string CityPrefix(string city)
    {
        var letters = new string(city.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return letters.Length >= 3 ? letters[..3] : letters.PadRight(3, 'X');
    }

    public static string Slug(string s) =>
        Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

public static partial class Formats
{
    private static readonly string[] TimeFormats = ["hh:mm tt", "h:mm tt", "HH:mm"];

    public static bool TryParseTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim().ToUpperInvariant(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    public static string Time(TimeOnly t) => t.ToString("hh:mm tt", CultureInfo.InvariantCulture);

    public static bool TryParseDate(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\s*(?:(\d{1,2})\s*h)?\s*(?:(\d{1,2})\s*m)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    /// <summary>Parses "4h 30m", "9h", "45m".</summary>
    public static bool TryParseDuration(string? s, out int minutes)
    {
        minutes = 0;
        var m = DurationPattern().Match(s ?? "");
        if (!m.Success || (!m.Groups[1].Success && !m.Groups[2].Success)) return false;
        var h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (min >= 60) return false;
        minutes = h * 60 + min;
        return minutes > 0;
    }

    public static string Duration(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60}h" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";

    public static string MapsDirections(double? lat, double? lng, string fallbackQuery) =>
        lat is not null && lng is not null
            ? FormattableString.Invariant($"https://www.google.com/maps/dir/?api=1&destination={lat},{lng}")
            : "https://www.google.com/maps/dir/?api=1&destination=" + Uri.EscapeDataString(fallbackQuery);
}

/// <summary>Ghana runs on GMT with no daylight saving, so "today" is the UTC date.</summary>
public static class GhanaClock
{
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
