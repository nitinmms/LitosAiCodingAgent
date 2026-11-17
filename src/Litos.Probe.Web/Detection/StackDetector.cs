using System.Text.RegularExpressions;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Profiles;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Litos.Probe.Web.Detection;

public enum DetectionStatus
{
    NotAWebApp,

    /// <summary>A web framework the factory has no toolchain for yet.</summary>
    StackNotSupported,

    Detected,

    /// <summary>Several web apps (a monorepo): a person picks one.</summary>
    Several,
}

/// <param name="Directory">Relative to the repository, with '/' separators; "" for the root.</param>
/// <param name="Evidence">The files whose markers matched.</param>
public sealed record DetectionCandidate(string ProfileId, int Tier, string Directory, IReadOnlyList<string> Evidence);

public sealed record DetectionResult(DetectionStatus Status, IReadOnlyList<DetectionCandidate> Candidates)
{
    public DetectionCandidate? Best => Candidates.FirstOrDefault(c => c.Tier != StackProfiles.NotSupportedTier);
}

public interface IStackDetector
{
    DetectionResult Detect(string repoRoot, IEnumerable<StackProfile> profiles);
}

/// <summary>
/// Matches every profile's markers against a repository (testability-architecture.md §6.1, step
/// 1). A profile's first marker finds candidate folders; its other markers must then hold within
/// each folder, so a monorepo's <c>web/</c> and <c>api/</c> are reported separately.
/// </summary>
public sealed class StackDetector : IStackDetector
{
    private static readonly string[] Ignored = ["**/node_modules/**", "**/bin/**", "**/obj/**", "**/.git/**", "**/dist/**", "**/.probe-web/**", "**/.factory/**"];
    private const int MaxBytesRead = 1_000_000;

    public DetectionResult Detect(string repoRoot, IEnumerable<StackProfile> profiles)
    {
        var root = Path.GetFullPath(repoRoot);
        var candidates = new List<(DetectionCandidate Candidate, int Score)>();

        foreach (var profile in profiles.Where(p => !p.Detect.IsEmpty))
        {
            var rules = profile.Detect;
            var seed = rules.All.Count > 0 ? [rules.All[0]] : rules.Any;
            var folders = seed.SelectMany(m => Matches(root, "", m)).Select(FolderOf).Distinct(StringComparer.Ordinal);

            foreach (var folder in folders)
            {
                var all = rules.All.Select(m => Matches(root, folder, m).FirstOrDefault()).ToList();
                if (all.Any(f => f is null))
                    continue;
                var any = rules.Any.Select(m => Matches(root, folder, m).FirstOrDefault()).Where(f => f is not null).ToList();
                if (rules.Any.Count > 0 && any.Count == 0)
                    continue;
                if (rules.None.Any(m => Matches(root, folder, m).Any()))
                    continue;

                var evidence = all.Concat(any).Select(f => f!).Distinct(StringComparer.Ordinal).ToList();
                candidates.Add((new DetectionCandidate(profile.Id, profile.Tier, folder, evidence), evidence.Count));
            }
        }

        // The most specific match first (most markers), then the best-proved tier; a stack we can't
        // run yet goes last.
        var ordered = candidates
            .OrderBy(c => c.Candidate.Tier == StackProfiles.NotSupportedTier)
            .ThenBy(c => c.Candidate.Directory.Length)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Candidate.Tier)
            .Select(c => c.Candidate)
            .ToList();
        // One app per folder: a more specific profile hides a weaker match on the same folder.
        var perFolder = ordered.GroupBy(c => c.Directory, StringComparer.Ordinal).Select(g => g.First()).ToList();

        var supported = perFolder.Count(c => c.Tier != StackProfiles.NotSupportedTier);
        var status = supported switch
        {
            0 when perFolder.Count > 0 => DetectionStatus.StackNotSupported,
            0 => DetectionStatus.NotAWebApp,
            1 => DetectionStatus.Detected,
            _ => DetectionStatus.Several,
        };
        return new DetectionResult(status, perFolder);
    }

    /// <summary>The repository-relative files under <paramref name="folder"/> that match the marker.</summary>
    private static IEnumerable<string> Matches(string root, string folder, DetectMarker marker)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(marker.Glob);
        matcher.AddExcludePatterns(Ignored);
        var directory = Path.Combine(root, folder);
        if (!Directory.Exists(directory))
            return [];

        var pattern = marker.Pattern is null ? null : new Regex(marker.Pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(2));
        return matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(directory))).Files
            .Select(f => Join(folder, f.Path))
            .Order(StringComparer.Ordinal)
            .Where(f => pattern is null || pattern.IsMatch(ReadHead(Path.Combine(root, f))));
    }

    private static string ReadHead(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[MaxBytesRead];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string FolderOf(string file)
    {
        var slash = file.LastIndexOf('/');
        return slash < 0 ? "" : file[..slash];
    }

    private static string Join(string folder, string relative) => folder.Length == 0 ? relative : $"{folder}/{relative}";
}
