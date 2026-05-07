using System.Text.RegularExpressions;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Provides input sanitization and validation for RFID UIDs.
/// Prevents "RFID Injection" attacks by enforcing strict format rules
/// before any data is transmitted to the backend API.
/// </summary>
public static partial class InputSanitizer
{
    /// <summary>
    /// Compiled regex: 1–20 alphanumeric characters.
    /// Covers any RFID code format — short numeric IDs (e.g. 00),
    /// decimal IDs (e.g. 0524370791) and hex UIDs (e.g. 08FF2017)
    /// from any RFID reader, regardless of vendor or encoding scheme.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9]{1,20}$")]
    private static partial Regex ValidRfidPattern();

    /// <summary>
    /// Validates that the given UID matches the expected RFID format.
    /// </summary>
    public static bool IsValidRfidUid(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return false;

        return ValidRfidPattern().IsMatch(uid);
    }

    /// <summary>
    /// Strips all non-alphanumeric characters from the raw input,
    /// then trims leading/trailing whitespace.
    /// </summary>
    public static string SanitizeRfidUid(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return string.Empty;

        return StripNonAlphanumeric().Replace(uid.Trim(), string.Empty);
    }

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex StripNonAlphanumeric();
}
