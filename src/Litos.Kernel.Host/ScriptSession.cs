using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Litos.Kernel.Host;

/// <summary>
/// Owns the persisted ScriptState across many eval calls within one subprocess lifetime — the
/// direct C# analog of a Python/Node REPL process (§4.3). Runs code via Script.ContinueWithAsync,
/// which carries the whole submission chain (locals, usings, local functions) forward.
/// </summary>
public sealed class ScriptSession
{
    private static readonly string[] Imports =
        ["System", "System.IO", "System.Linq", "System.Text.Json", "System.Net.Http", "System.Collections.Generic"];

    private readonly KernelGlobals _globals;
    private readonly KernelStateReflector _stateReflector;
    private readonly FunctionRegistry _functionRegistry = new();
    private readonly string _scratchDirectory;

    private ScriptOptions _options;
    private ScriptState<object>? _state;

    public ScriptSession(string scratchDirectory, ToolBridge bridge, IReadOnlyList<BridgedToolSchema> bridgedTools)
    {
        _scratchDirectory = scratchDirectory;
        // KernelSession also creates this before spawning the subprocess, but the host must not
        // depend on that — a script's very first eval may write here before anything else has, and
        // "SCRATCH_DIR exists" should be an invariant of this class's own construction, not an
        // assumption about the caller.
        Directory.CreateDirectory(scratchDirectory);
        _stateReflector = new KernelStateReflector();
        _globals = new KernelGlobals(scratchDirectory, _stateReflector);

        var bridgeSource = ToolWrapperCodeGen.Generate(bridgedTools);

        _options = ScriptOptions.Default
            .WithImports(Imports)
            .WithReferences(
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(Enumerable).Assembly,
                typeof(JsonSerializer).Assembly,
                typeof(System.Net.Http.HttpClient).Assembly,
                typeof(Uri).Assembly,
                typeof(System.Collections.ICollection).Assembly,
                typeof(ScriptSession).Assembly) // so the bootstrap's generated wrapper functions can reference global::Litos.Kernel.Host.ScriptSession.BridgeField
            .WithEmitDebugInformation(false);

        // The bridge wrapper functions and the __ToolBridge field they close over are injected as
        // a preliminary submission run once at construction, ahead of any real EvalRequest — this
        // keeps ToolWrapperCodeGen's generated source out of every StateDelta diff (it isn't
        // model-written, so it shouldn't show up as "new state" the model needs to know about) and
        // means _functionRegistry only ever scans model-submitted code from here on.
        BridgeField = bridge;
        _bootstrapSource = bridgeSource;
    }

    // Set via the constructor's captured bridge; exposed as a static-ish field the generated code
    // references by name (see ToolWrapperCodeGen) since Roslyn scripting globals must be one fixed
    // object graph (KernelGlobals) decided at ScriptOptions build time — an imperative field
    // assignment inside the bootstrap submission is the simplest way to smuggle the bridge instance
    // in without adding it to the public KernelGlobals surface the model's script can see/mutate.
    public static ToolBridge? BridgeField;
    private readonly string _bootstrapSource;
    private bool _bootstrapped;

    public async Task EnsureBootstrappedAsync()
    {
        if (_bootstrapped)
            return;
        _state = await CSharpScript.RunAsync(_bootstrapSource, _options, _globals);
        _bootstrapped = true;
    }

    public async Task<EvalResult> EvalAsync(string requestId, string code, ToolBridge bridge, CancellationToken ct)
    {
        await EnsureBootstrappedAsync();
        bridge.ResetEvalBudget();

        var variablesBefore = _state!.Variables.Select(v => v.Name).ToHashSet();
        var functionsBefore = new List<string>();

        var originalOut = Console.Out;
        var captured = new StringBuilder();
        Console.SetOut(new StringWriter(captured));

        bool isError;
        string? errorText = null;
        object? returnValue = null;

        try
        {
            _state = await _state.ContinueWithAsync<object>(code, _options, ct);
            returnValue = _state.ReturnValue;
            isError = false;
        }
        catch (CompilationErrorException ex)
        {
            isError = true;
            errorText = string.Join(Environment.NewLine, ex.Diagnostics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            isError = true;
            errorText = ex.Message;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var functionDiff = _functionRegistry.ScanAndDiff(code);
        _stateReflector.Current = _state;
        _stateReflector.Functions = _functionRegistry;

        var variableDiff = _state!.Variables
            .Where(v => !variablesBefore.Contains(v.Name))
            .Select(v => $"variable {v.Name} ({DescribeShape(v.Value)})")
            .ToList();
        var functionDiffText = functionDiff.Select(f => $"function {f.Signature}").ToList();
        var stateDelta = BuildStateDelta(variableDiff, functionDiffText);

        var (output, outputTruncated, outputArtifact) = CapText(captured.ToString(), KernelLimits.MaxOutputBytes, requestId, "output");
        var (returnText, returnTruncated, returnArtifact) = isError
            ? (null, false, (string?)null)
            : CapText(SerializeReturnValue(returnValue), KernelLimits.MaxReturnValueBytes, requestId, "return");

        return new EvalResult(
            requestId,
            Output: output,
            ReturnValueText: isError ? errorText : returnText,
            IsError: isError,
            Truncated: outputTruncated || returnTruncated,
            ArtifactPath: outputArtifact ?? returnArtifact,
            StateDelta: stateDelta);
    }

    private string? BuildStateDelta(List<string> variableDiff, List<string> functionDiff)
    {
        var all = functionDiff.Concat(variableDiff).ToList();
        if (all.Count == 0)
            return null;

        var shown = all.Take(KernelLimits.MaxStateDeltaNames).ToList();
        var text = "[kernel state changed this round: " + string.Join(", ", shown.Select(s => "+" + s)) + "]";
        if (all.Count > shown.Count)
            text += $" (+{all.Count - shown.Count} more — call KernelState.List() for the full set)";

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= KernelLimits.MaxStateDeltaBytes)
            return text;

        // Degrade further if even the capped-name-count text overflows the byte budget.
        while (shown.Count > 1 && Encoding.UTF8.GetByteCount(
            "[kernel state changed this round: " + string.Join(", ", shown.Select(s => "+" + s)) + $"] (+{all.Count - shown.Count} more — call KernelState.List() for the full set)") > KernelLimits.MaxStateDeltaBytes)
        {
            shown.RemoveAt(shown.Count - 1);
        }
        return "[kernel state changed this round: " + string.Join(", ", shown.Select(s => "+" + s)) + $"] (+{all.Count - shown.Count} more — call KernelState.List() for the full set)";
    }

    private static string DescribeShape(object? value) =>
        value switch
        {
            null => "null",
            string s => $"string, {s.Length} chars",
            System.Collections.ICollection c => $"~{c.Count} items",
            System.Collections.IEnumerable => "IEnumerable",
            _ => value.GetType().Name,
        };

    /// <summary>
    /// §8.2's return-value serialization semantics: primitives pass through directly; anything
    /// else is JSON-serialized (never a bare .ToString(), which produces the unhelpful CLR default
    /// for most non-primitive types); a type System.Text.Json can't handle returns a short
    /// diagnostic instead of crashing the eval or falling back to .ToString(); an IEnumerable is
    /// never auto-enumerated to completion — capped at MaxEnumerableElements with a truncation note.
    /// </summary>
    private static string? SerializeReturnValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return value.ToString();
            case System.Collections.IEnumerable enumerable and not string:
                return SerializeEnumerable(enumerable);
        }

        try
        {
            return JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions { WriteIndented = false });
        }
        catch (NotSupportedException)
        {
            return $"value of type '{value.GetType().FullName}' is not serializable — assign it to a named variable to keep it in kernel state instead of returning it";
        }
    }

    private static string SerializeEnumerable(System.Collections.IEnumerable enumerable)
    {
        var items = new List<object?>();
        var truncated = false;
        var count = 0;
        foreach (var item in enumerable)
        {
            count++;
            if (items.Count < KernelLimits.MaxEnumerableElements)
                items.Add(item);
            else
            {
                truncated = true;
                break;
            }
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(items);
        }
        catch (NotSupportedException)
        {
            return $"an IEnumerable of {typeof(object)} elements is not serializable — assign it to a named variable instead of returning it";
        }

        return truncated
            ? json[..^1] + $"] (truncated at {KernelLimits.MaxEnumerableElements} elements)"
            : json;
    }

    private (string? Text, bool Truncated, string? ArtifactPath) CapText(string? text, int maxBytes, string requestId, string kind)
    {
        if (text is null)
            return (null, false, null);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= maxBytes)
            return (text, false, null);

        Directory.CreateDirectory(_scratchDirectory);
        var artifactPath = Path.Combine(_scratchDirectory, $"eval-{requestId}-{kind}.txt");
        File.WriteAllText(artifactPath, text);

        // Slice by UTF-16 chars is an approximation of the byte cap (fine for the preview — the
        // full untruncated content is already safely on disk at artifactPath).
        var previewChars = Math.Min(text.Length, maxBytes);
        var preview = text[..previewChars] + $"\n...[truncated, full content at {artifactPath}]";
        return (preview, true, artifactPath);
    }
}
