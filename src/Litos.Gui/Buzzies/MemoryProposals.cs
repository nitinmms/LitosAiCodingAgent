using System.Text;
using System.Text.Json;

namespace Litos.Gui.Buzzies;

/// <summary>A memory a Buzzy wanted to keep while nobody was watching, waiting for the user.</summary>
public sealed record MemoryProposal(string Id, string Note, string Source, DateTimeOffset ProposedAt);

/// <summary>
/// Memories proposed during unattended routine runs (Implement_Buzzies.md, Phase 2). A scheduled
/// run reads web pages and files nobody has checked, so what it wants to remember is held here,
/// with where it came from, until the user keeps or discards it; only a kept proposal reaches
/// MEMORY.md, the file that goes into every later prompt. Stored per Buzzy as
/// <c>memory-proposals.json</c>; small, so read and written whole under a lock.
/// </summary>
public sealed class MemoryProposals(BuzzyStore store)
{
    public const string FileName = "memory-proposals.json";
    internal const int MaxPending = 50;

    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private string PathOf(string buzzyId) => Path.Combine(store.FolderOf(buzzyId), FileName);

    public IReadOnlyList<MemoryProposal> List(string buzzyId)
    {
        lock (Gate)
            return Load(buzzyId);
    }

    /// <summary>Adds a proposal. False when the Buzzy already has <see cref="MaxPending"/> waiting.</summary>
    public bool Propose(string buzzyId, string note, string source, DateTimeOffset now)
    {
        lock (Gate)
        {
            var pending = Load(buzzyId);
            if (pending.Count >= MaxPending)
                return false;
            if (pending.Any(p => p.Note == note))
                return true;
            Save(buzzyId, [.. pending, new MemoryProposal(Guid.NewGuid().ToString("n"), note, source, now)]);
            return true;
        }
    }

    /// <summary>Keeps a proposal: appends it to MEMORY.md, saying where it came from, and removes it.</summary>
    public void Keep(string buzzyId, string proposalId)
    {
        lock (Gate)
        {
            var pending = Load(buzzyId);
            if (pending.FirstOrDefault(p => p.Id == proposalId) is not { } proposal)
                return;
            File.AppendAllText(store.MemoryPathOf(buzzyId),
                RememberTool.Line(proposal.ProposedAt, $"from {proposal.Source}, kept by you", proposal.Note), Utf8NoBom);
            Save(buzzyId, [.. pending.Where(p => p.Id != proposalId)]);
        }
    }

    public void Discard(string buzzyId, string proposalId)
    {
        lock (Gate)
            Save(buzzyId, [.. Load(buzzyId).Where(p => p.Id != proposalId)]);
    }

    private List<MemoryProposal> Load(string buzzyId)
    {
        try
        {
            var path = PathOf(buzzyId);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<MemoryProposal>>(File.ReadAllText(path), JsonOptions) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(string buzzyId, List<MemoryProposal> proposals)
    {
        var path = PathOf(buzzyId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(proposals, JsonOptions), Utf8NoBom);
    }
}
