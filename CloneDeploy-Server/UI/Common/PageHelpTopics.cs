using System.Globalization;
using System.Text.Json;

namespace CloneDeploy_Server.UI.Common;

public static class PageHelpTopics
{
    public const string DefaultRoot = "https://docs.clonedeploy.com/";
    private static readonly Topic[] Topics = LoadTopics();
    private static readonly Topic Home = new("/", "CloneDeploy", "index.html");

    public sealed record Topic(string Route, string Title, string Href);

    public static Topic Resolve(string relativePath)
    {
        var segments = relativePath.Split('?', '#')[0].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Topics.FirstOrDefault(topic => Matches(topic.Route, segments)) ?? Home;
    }

    public static string GetUrl(Topic topic, string? configuredRoot)
    {
        var root = Uri.TryCreate(configuredRoot, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                   && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? uri.AbsoluteUri
            : DefaultRoot;

        return new Uri(new Uri(root.TrimEnd('/') + "/"), topic.Href).AbsoluteUri;
    }

    private static Topic[] LoadTopics()
    {
        using var stream = typeof(PageHelpTopics).Assembly.GetManifestResourceStream("CloneDeploy.HelpTopics.json")
                           ?? throw new InvalidOperationException("The help topic map is missing.");
        var topics = JsonSerializer.Deserialize<Topic[]>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("The help topic map is empty.");

        return topics.OrderByDescending(topic => topic.Route.Split('/').Count(segment =>
                segment.Length > 0 && !segment.StartsWith('{')))
            .ToArray();
    }

    private static bool Matches(string route, string[] segments)
    {
        var pattern = route.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pattern.Length != segments.Length)
            return false;

        for (var index = 0; index < pattern.Length; index++)
        {
            var part = pattern[index];
            if (!part.StartsWith('{'))
            {
                if (!string.Equals(part, segments[index], StringComparison.OrdinalIgnoreCase))
                    return false;

                continue;
            }

            if (part.EndsWith(":int}", StringComparison.Ordinal)
                && !int.TryParse(segments[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return false;

            if (part.EndsWith(":guid}", StringComparison.Ordinal) && !Guid.TryParse(segments[index], out _))
                return false;
        }

        return true;
    }
}
