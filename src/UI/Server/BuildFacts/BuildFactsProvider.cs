using System.Reflection;

namespace ClearMeasure.Bootcamp.UI.Server.BuildFacts;

/// <summary>
/// Holds the answer of <c>GET /_build</c> for the life of the process: <c>build-facts.json</c> of the content root
/// when the release pipeline stamped one, otherwise the same shape with the version of the assembly and nulls
/// (a local run). The file is read once, on the first request.
/// </summary>
public sealed class BuildFactsProvider
{
    /// <summary>Name of the file the release pipeline stamps into the published app.</summary>
    public const string FileName = "build-facts.json";

    private readonly Lazy<string> _json;

    /// <summary>
    /// Reads the facts from the content root of <paramref name="environment"/>.
    /// </summary>
    public BuildFactsProvider(IHostEnvironment environment, ILogger<BuildFactsProvider> logger)
        : this(
            environment.ContentRootPath,
            typeof(BuildFactsProvider).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            logger)
    {
    }

    /// <summary>
    /// Reads the facts from <paramref name="contentRootPath"/>; <paramref name="informationalVersion"/> is the
    /// version to answer when they name none.
    /// </summary>
    internal BuildFactsProvider(string contentRootPath, string? informationalVersion, ILogger logger)
    {
        var path = Path.Combine(contentRootPath, FileName);
        _json = new Lazy<string>(() => Load(path, informationalVersion, logger));
    }

    /// <summary>The answer, as JSON.</summary>
    public string Json => _json.Value;

    private static string Load(string path, string? informationalVersion, ILogger logger)
    {
        if (!File.Exists(path))
        {
            return BuildFactsDocument.Create(null, informationalVersion);
        }

        string? content = null;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Build facts file {Path} could not be read", path);
        }

        var stamped = BuildFactsDocument.Parse(content);
        if (stamped is null && content is not null)
        {
            logger.LogWarning("Build facts file {Path} is not a JSON object; answering without it", path);
        }

        return BuildFactsDocument.Create(stamped, informationalVersion);
    }
}
