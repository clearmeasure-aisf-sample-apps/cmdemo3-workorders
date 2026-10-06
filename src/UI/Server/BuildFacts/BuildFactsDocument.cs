using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClearMeasure.Bootcamp.UI.Server.BuildFacts;

/// <summary>
/// Shapes the answer of <c>GET /_build</c>: always the same properties, in the same order, each one null when the
/// build could not tell. The content comes from <c>build-facts.json</c>, which the release pipeline stamps into the
/// published app (<c>scripts/Write-BuildFacts.ps1</c>); the app adds nothing but the version when the file has none.
/// </summary>
internal static class BuildFactsDocument
{
    private const string VersionProperty = "version";

    // The contract with the dashboard. A property the stamped file has beyond these follows them, unchanged.
    private static readonly string[] Properties =
    [
        VersionProperty,
        "commit",
        "commitUrl",
        "builtAt",
        "buildUrl",
        "code",
        "tests",
        "coverage",
        "complexity",
        "crap",
        "analysis"
    ];

    /// <summary>
    /// Returns the stamped facts as a JSON object, or null when <paramref name="json"/> is absent or is not one.
    /// </summary>
    public static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes the answer: the facts of <paramref name="stamped"/> when there are any, and the version of the
    /// assembly (<paramref name="informationalVersion"/>, without its source revision) when they name none.
    /// </summary>
    public static string Create(JsonObject? stamped, string? informationalVersion)
    {
        var document = new JsonObject();
        foreach (var name in Properties)
        {
            document[name] = stamped?[name]?.DeepClone();
        }

        document[VersionProperty] ??= ToVersion(informationalVersion);
        if (stamped is null)
        {
            return document.ToJsonString();
        }

        foreach (var (name, value) in stamped.Where(property => !Properties.Contains(property.Key)))
        {
            document[name] = value?.DeepClone();
        }

        return document.ToJsonString();
    }

    // "2.4.15+0123abc" (the SDK appends the commit to the informational version) -> "2.4.15".
    private static string? ToVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var metadata = informationalVersion.IndexOf('+');
        return metadata < 0 ? informationalVersion : informationalVersion[..metadata];
    }
}
