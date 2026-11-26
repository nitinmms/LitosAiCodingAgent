using System.Text.RegularExpressions;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>
/// The Buzzy-creation invariant (Implement_Buzzies.md, Phase 3). Creation used to be inferred from
/// what the user typed, which hijacked real requests in the plain chat — "add an assistant to the
/// checkout page" opened a creation card instead of reaching the coding agent. It is now reachable
/// only from the explicit <c>/buzzy</c> command, and this test states that as a rule about the source
/// rather than a corpus of sentences that happens to pass: a sentence pattern can only ever be
/// examples, while "no other caller exists" is a property.
/// </summary>
public class BuzzyCreationInvariantTests
{
    [Fact]
    public void CreationIsOnlyReachedFromTheBuzzyCommand()
    {
        var gui = Path.Combine(RepositoryRoot(), "src", "Litos.Gui");
        var callers = Files(gui)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (File: Path.GetFileName(file), Number: index + 1, Text: line.Trim())))
            // The declaration and the one call inside the command handler are the only mentions.
            .Where(x => Regex.IsMatch(x.Text, @"\bProposeBuzzyAsync\b"))
            .ToArray();

        var declarations = callers.Count(c => c.Text.Contains("private async Task ProposeBuzzyAsync"));
        var calls = callers.Count(c => c.Text.Contains("await ProposeBuzzyAsync("));

        Assert.Equal(1, declarations);
        Assert.Equal(1, calls);

        // That single call must be in the /buzzy handler, and nowhere else.
        var handler = File.ReadAllLines(Path.Combine(gui, "MainWindow.Grants.cs"));
        var handlerStart = Array.FindIndex(handler, l => l.Contains("private async Task HandleBuzzyCommandAsync"));
        Assert.True(handlerStart >= 0, "HandleBuzzyCommandAsync should exist.");
        var handlerEnd = Array.FindIndex(handler, handlerStart, l => l.Trim() == "}" && l.Length > 0 && l.StartsWith("    }"));
        var handlerBody = string.Join("\n", handler.Skip(handlerStart).Take(Math.Max(1, handlerEnd - handlerStart)));
        Assert.Contains("await ProposeBuzzyAsync(", handlerBody);
    }

    [Fact]
    public void TheSendPath_CannotStartACreationProposal()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Litos.Gui", "MainWindow.axaml.cs"));
        var start = source.IndexOf("private async Task SubmitAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task RunTurnFromTextAsync", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "SubmitAsync should be findable in MainWindow.axaml.cs.");

        var submit = source[start..end];

        // Neither the creation entry point nor any intent heuristic appears on the send path.
        Assert.DoesNotContain("ProposeBuzzyAsync", submit);
        Assert.DoesNotContain("LooksLikeCreation", submit);
    }

    [Fact]
    public void TheProposer_ExposesNoIntentGuessing()
    {
        var members = typeof(Litos.Gui.Buzzies.BuzzyProposer)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)
            .ToArray();

        Assert.DoesNotContain(members, name =>
            name.Contains("Looks", StringComparison.Ordinal)
            || name.Contains("Intent", StringComparison.Ordinal)
            || name.Contains("IsCreation", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBuzzyCommand_IsListedInTheCommandMenu()
    {
        // SlashCommands.All and the dispatch switch are kept in sync by hand (see its own doc
        // comment), so an unlisted command would be undiscoverable.
        var listed = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Litos.Gui", "SlashCommand.cs"));
        var dispatched = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Litos.Gui", "MainWindow.axaml.cs"));

        Assert.Contains("\"/buzzy\"", listed);
        Assert.Contains("case \"/buzzy\":", dispatched);
    }

    private static IEnumerable<string> Files(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            // Build output folders can hold generated copies of the sources.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LitosAiAgent.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }
}