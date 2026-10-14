using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Jobs.Api.Http;

/// <summary>The job's <c>Version</c> travels as a strong ETag, for example <c>"7"</c>.</summary>
internal static class ETags
{
    public const string IfMatchHeader = "If-Match";

    public static string Format(int version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Parses If-Match into a version. Missing or <c>*</c> gives 428 (a specific version is required);
    /// weak tags, lists and non-numeric tags give 400.
    /// </summary>
    public static bool TryParseIfMatch(string? header, out int version, [NotNullWhen(false)] out IResult? problem)
    {
        version = 0;
        problem = null;
        var value = header?.Trim();

        if (string.IsNullOrEmpty(value) || value == "*")
        {
            problem = Problems.PreconditionRequired();
            return false;
        }

        var isQuoted = value.Length >= 3 && value[0] == '"' && value[^1] == '"';
        if (!isQuoted || !int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            version = 0;
            problem = Problems.InvalidIfMatch();
            return false;
        }

        return true;
    }
}
