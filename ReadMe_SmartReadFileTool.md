# Smart Read File Tool for Litos

## Purpose

This document proposes a smarter `read_file` architecture for **Litos** that prevents coding agents from repeatedly reading files whose contents have not changed since the agent last saw them.

The primary objective is **not filesystem I/O reduction**. The larger problem is repeatedly returning the same file contents to the LLM, which wastes:

- Context tokens
- Inference time
- Tool-call steps
- Prompt budget
- Local-model performance
- Agent step budget

The recommended solution is to make `read_file` **deterministically cache-aware at the tool layer**, rather than relying only on prompting the model to avoid duplicate reads.

---

# 1. Core Principle

Do not depend on the LLM to remember:

> "I already read this file."

Instead, let the Litos tool runtime know:

```text
Path + File Version + Read Range + Context Generation
```

Example:

```text
File: src/Services/OrderService.cs
Version: A82F91
Previously read lines: 1-240
Context generation: 12
```

When the agent calls:

```text
read_file("src/Services/OrderService.cs")
```

Litos checks whether the file has changed since the previous read.

If it has not changed and the relevant content is still considered available to the model, return a very small response instead of the complete file.

Example:

```text
UNCHANGED

src/Services/OrderService.cs has not changed since read r_104.
Previously read: lines 1-240
Version: A82F91

Reuse the previously read content.
```

This may cost only tens of tokens instead of thousands.

---

# 2. Recommended Architecture

```text
                    LLM
                     |
                     | read_file("OrderService.cs")
                     v
             +------------------+
             | Litos Tool Layer |
             +---------+--------+
                       |
                       v
              FileReadLedger Lookup
                       |
              +--------+--------+
              |                 |
         SAME VERSION       NEW VERSION
              |                 |
              v                 v
       Return compact      Read file / diff
       UNCHANGED result         |
                                v
                         Update FileReadLedger
```

The important rule is:

> The optimization must be enforced by the tool runtime, not merely requested in the system prompt.

---

# 3. FileReadLedger

Litos should maintain a session-level record of files and ranges the agent has already seen.

A possible C# model:

```csharp
public sealed record FileReadState(
    string Path,
    string ContentHash,
    long Length,
    DateTime LastWriteUtc,
    IReadOnlyList<LineRange> CoveredRanges,
    long ContextGeneration,
    string ReadId);

public sealed record LineRange(
    int StartLine,
    int EndLine);
```

An in-memory ledger might look conceptually like this:

```text
PreviouslyReadFiles
----------------------------------------------------------------
File                         Version     Lines        Context Gen
OrderService.cs              A82F91      1-240        12
OrderRepository.cs           772CD2      1-180        12
Program.cs                   92DA10      1-95         12
BigFile.cs                   8A17FF      1-200        11
                                         450-600
                                         800-900
```

The ledger should live outside the LLM conversation.

---

# 4. Determining Whether a File Changed

Do not rely only on Git status.

A file can change because of:

- Litos itself
- The IDE
- A formatter
- A code generator
- A build process
- Git checkout/rebase
- Another application
- A human editing the file
- A background process

The reliable identity is the file's content.

## Recommended fingerprint

For local source files, `System.IO.Hashing.XxHash3` is a strong choice because it is extremely fast and sufficient for change detection.

Example:

```csharp
using System.IO.Hashing;

public static ulong GetFileHash(string path)
{
    using var stream = File.OpenRead(path);
    return XxHash3.HashToUInt64(stream);
}
```

A cryptographic hash such as SHA-256 is unnecessary unless Litos has another requirement for cryptographic integrity.

---

# 5. Fast Change Detection

For maximum efficiency, Litos can use metadata as a fast first-level check.

```text
read_file requested
      |
      v
Compare file Length + LastWriteUtc
      |
  +---+---+
  |       |
Same    Different
  |       |
  v       v
Likely   Compute
same     content hash
  |       |
  v       v
Skip     Compare hash
```

However, filesystem reads are usually much cheaper than reinjecting a large source file into an LLM.

Therefore the optimization priority should be:

```text
1. Reduce LLM tokens
2. Reduce unnecessary agent steps
3. Reduce filesystem reads
```

not the reverse.

---

# 6. Duplicate Read Behavior

Suppose the agent first performs:

```text
read_file("OrderService.cs")
```

and receives 300 lines.

Later it performs the same call again without any intervening change.

Instead of returning the file again:

```json
{
  "status": "unchanged",
  "path": "src/Services/OrderService.cs",
  "version": "A82F91",
  "previousReadId": "read_104",
  "coveredLines": [
    {
      "start": 1,
      "end": 300
    }
  ]
}
```

The user-facing/model-facing representation can be compact:

```text
UNCHANGED: OrderService.cs

This file has not changed since read_104.
Lines 1-300 were already provided.
Version: A82F91
```

---

# 7. Partial Reads Must Be Tracked

This is essential.

Suppose the model previously requested:

```text
read_file("BigFile.cs", 1, 200)
```

Later it requests:

```text
read_file("BigFile.cs", 500, 700)
```

Litos must not claim the entire file has already been read.

Track line intervals.

Example:

```text
BigFile.cs
Version: ABC123

Previously seen:
1-200
450-600
800-900
```

Then Litos can behave intelligently:

```text
Request 100-150  -> return UNCHANGED
Request 201-300  -> return 201-300
Request 550-650  -> return only 601-650
Request 1-1000   -> return only unseen ranges if safe
```

This gives significant savings on large source files.

---

# 8. Range Merging

The ledger should merge overlapping or adjacent ranges.

For example:

```text
Existing:
1-100
101-200
150-300
```

becomes:

```text
1-300
```

Simple range merging makes coverage checks fast.

Possible C# representation:

```csharp
public sealed class ReadCoverage
{
    private readonly List<LineRange> _ranges = new();

    public IReadOnlyList<LineRange> Ranges => _ranges;

    public void Add(int start, int end)
    {
        // Merge overlapping and adjacent ranges.
    }

    public bool Contains(int start, int end)
    {
        // Return true if the complete requested range
        // was already provided for the same file version.
    }
}
```

---

# 9. Litos Edits Should Update the Ledger

This is one of the highest-value improvements.

A common coding-agent pattern is:

```text
READ
EDIT
READ
EDIT
READ
BUILD
READ
```

The post-edit `READ` is often unnecessary.

If Litos itself performs:

```text
edit_file("OrderService.cs", ...)
```

the tool runtime already knows:

- What lines changed
- What text was inserted
- What text was removed
- The resulting file
- The new file version

Therefore `edit_file`, `write_file`, and `apply_patch` should update the FileReadLedger automatically.

Example:

```text
edit_file
   |
   +-- Apply patch
   |
   +-- Calculate new file hash
   |
   +-- Update cached snapshot
   |
   +-- Update read coverage
   |
   +-- Return changed ranges
```

Tool output:

```text
Edit applied successfully.

File: src/Services/OrderService.cs
Previous version: A82F91
New version: B18272
Changed lines: 114-129

The updated file state is already known to Litos.
A full reread is unnecessary unless additional context is required.
```

---

# 10. Return Diffs When Files Change

If the agent previously read a large file and only a small part changed, returning the complete file again is wasteful.

Example:

```text
Previous version: A82F91
Current version: D1120C
```

Instead of returning 900 lines:

```diff
@@ lines 178-183 @@
- old code
+ new code
```

Recommended policy:

```text
Same version
    -> UNCHANGED

Changed slightly
    -> Return delta/diff

Changed substantially
    -> Return requested range normally
```

A configurable threshold can determine when a delta becomes too large.

Example:

```csharp
public sealed class SmartReadOptions
{
    public double MaxDiffRatio { get; init; } = 0.25;
}
```

If more than 25% of the requested region changed, returning the current requested range may be simpler than returning a diff.

---

# 11. Context Compaction Is the Critical Edge Case

The read ledger cannot blindly assume that because Litos supplied a file earlier, the model can still see it.

Example:

```text
Step 10:
read_file(OrderService.cs)

Step 60:
context compaction occurs

Step 75:
read_file(OrderService.cs)
```

The FileReadLedger might say:

```text
Already read.
```

But the actual active model context may no longer contain the source code.

This would be incorrect.

---

# 12. Introduce ContextGeneration

Litos should track a monotonically increasing context generation.

Example:

```text
ContextGeneration = 12
```

Every successful read records the generation:

```text
OrderService.cs
Version: A82F91
ContextGeneration: 12
Lines: 1-240
```

When context compaction occurs:

```text
ContextGeneration = 13
```

Now Litos knows that content supplied in generation 12 may no longer be directly available to the model.

Recommended behavior:

```text
File unchanged
+
requested range exists in CURRENT context generation
    -> return UNCHANGED

File unchanged
+
requested range exists only in OLDER context generation
    -> retrieve cached snapshot/range

File changed
    -> return diff or fresh content
```

This prevents a serious correctness bug.

---

# 13. Do Not Throw Away Cached File Snapshots During Compaction

The conversation can be compacted without discarding the external file cache.

Recommended architecture:

```text
+-----------------------------------+
| Workspace Snapshot Store          |
|                                   |
| OrderService.cs @ A82F91          |
| Program.cs      @ 92DA10          |
| Customer.cs     @ D99182          |
|                                   |
| Full source stored outside LLM    |
+-----------------+-----------------+
                  |
                  v
+-----------------------------------+
| Agent FileReadLedger              |
|                                   |
| OrderService.cs 1-240             |
| Program.cs      1-95              |
| Customer.cs     80-150            |
+-----------------+-----------------+
                  |
                  v
+-----------------------------------+
| Active LLM Context                |
|                                   |
| Only currently useful fragments   |
+-----------------------------------+
```

This separates:

1. Workspace state
2. Agent knowledge state
3. Current LLM context state

These should not be treated as the same thing.

---

# 14. Suggested Read Decision Algorithm

Pseudo-code:

```csharp
SmartReadResult ReadFile(
    string path,
    int? startLine,
    int? endLine,
    bool forceRefresh = false)
{
    var metadata = GetFileMetadata(path);

    var previous = ledger.Get(path);

    if (previous is null || forceRefresh)
        return ReadNormally(path, startLine, endLine);

    var currentHash = ResolveCurrentHash(path, metadata, previous);

    if (currentHash != previous.ContentHash)
        return ReadChangedFile(path, previous, startLine, endLine);

    var requestedRange = NormalizeRange(path, startLine, endLine);

    var currentContextCoverage =
        previous.GetCoverage(currentContextGeneration);

    if (currentContextCoverage.Contains(requestedRange))
    {
        return SmartReadResult.Unchanged(
            path,
            currentHash,
            requestedRange,
            previous.ReadId);
    }

    var cachedSnapshot = snapshotStore.Get(path, currentHash);

    if (cachedSnapshot is not null)
    {
        return ReturnCachedRange(
            cachedSnapshot,
            requestedRange);
    }

    return ReadNormally(path, startLine, endLine);
}
```

---

# 15. Proposed `read_file` Tool Contract

The model-facing API can remain simple.

Example request:

```json
{
  "path": "src/Services/OrderService.cs",
  "offset": 1,
  "limit": 200
}
```

Internally Litos may support:

```json
{
  "path": "src/Services/OrderService.cs",
  "offset": 1,
  "limit": 200,
  "forceRefresh": false
}
```

`forceRefresh` should normally remain hidden from or discouraged for the agent.

It should exist only for exceptional circumstances.

---

# 16. Structured Result Types

Recommended result statuses:

```text
content
unchanged
partial
changed
not_found
error
```

Examples:

## Normal content

```json
{
  "status": "content",
  "path": "src/OrderService.cs",
  "version": "A82F91",
  "startLine": 1,
  "endLine": 200,
  "content": "..."
}
```

## Unchanged

```json
{
  "status": "unchanged",
  "path": "src/OrderService.cs",
  "version": "A82F91",
  "previousReadId": "read_104",
  "coveredLines": "1-200"
}
```

## Partial cached coverage

```json
{
  "status": "partial",
  "path": "src/BigFile.cs",
  "version": "ABC123",
  "alreadyKnown": "500-600",
  "returned": "601-700",
  "content": "..."
}
```

## Changed

```json
{
  "status": "changed",
  "path": "src/OrderService.cs",
  "previousVersion": "A82F91",
  "currentVersion": "D1120C",
  "changedRanges": [
    "178-183"
  ],
  "diff": "..."
}
```

---

# 17. Add a `workspace_changes` Tool

Repeated rereading frequently happens because the model is uncertain about what changed.

Give the agent a cheap way to ask:

```text
workspace_changes()
```

Possible output:

```text
Changes since workspace checkpoint:

Modified:
- src/OrderService.cs
- src/OrderValidator.cs

Created:
- tests/OrderValidatorTests.cs

Deleted:
- none

Previously-read unchanged files:
- 17
```

This is much cheaper than the model deciding:

```text
I should reread the repository to see what changed.
```

---

# 18. Workspace Change Checkpoints

A workspace checkpoint could be created:

- At task start
- After planning
- After a batch of edits
- Before build/test
- After build/test if generators may modify files
- After Git operations

Example:

```text
Checkpoint C17
Repository state hashes captured
```

Then:

```text
workspace_changes(since: "C17")
```

can report changes cheaply.

---

# 19. External File Changes

Litos must detect changes made outside its own tools.

Possible strategies:

## Option A - Check on demand

Before serving a cached result:

1. Check metadata
2. Hash if needed
3. Compare against recorded version

This is simple and reliable.

## Option B - FileSystemWatcher

Use `.NET FileSystemWatcher` to invalidate cached versions proactively.

Example:

```csharp
var watcher = new FileSystemWatcher(repoRoot)
{
    IncludeSubdirectories = true,
    NotifyFilter =
        NotifyFilters.FileName |
        NotifyFilters.LastWrite |
        NotifyFilters.Size
};
```

However `FileSystemWatcher` should be treated as an optimization, not the final source of truth.

Events may be duplicated, coalesced, or missed depending on filesystem behavior.

Recommended approach:

```text
FileSystemWatcher
    -> marks cache entry "possibly dirty"

Next read
    -> verify using metadata/hash
```

---

# 20. Git Awareness

Git information is useful but should not replace content fingerprinting.

Possible optimizations:

- Invalidate affected files after checkout
- Invalidate after merge/rebase
- Record HEAD commit
- Detect staged/unstaged changes
- Use Git diff to produce compact deltas

But do not assume:

```text
git says clean
```

means the cached model context is automatically valid.

Git state and agent-read state are separate concepts.

---

# 21. Generated and Build Files

Some directories should usually be excluded from smart-read tracking or assigned special policy.

Examples:

```text
bin/
obj/
node_modules/
dist/
build/
coverage/
.git/
```

The agent generally should not repeatedly inspect these unless explicitly required.

Configuration:

```json
{
  "smartRead": {
    "exclude": [
      "**/bin/**",
      "**/obj/**",
      "**/node_modules/**",
      "**/.git/**"
    ]
  }
}
```

---

# 22. Large Files

For large files, avoid reading everything by default.

Recommended flow:

```text
read_file(large file)
        |
        v
Return structure / outline first
        |
        v
Agent requests relevant range
        |
        v
Track only requested ranges
```

For supported languages Litos can eventually integrate syntax-aware indexing.

For C# this could include:

- Namespace list
- Type list
- Method list
- Property list
- Symbol line ranges

Then the agent can request:

```text
read_symbol("OrderService.CalculateTotal")
```

rather than rereading the entire source file.

---

# 23. Interaction With Search Tools

A search result should not automatically mark the complete file as read.

Example:

```text
search("CalculateTotal")
```

returns:

```text
OrderService.cs:178
```

Only the returned snippet/range should count as seen.

The FileReadLedger could track sources:

```text
ReadSource:
- FullRead
- RangeRead
- SearchSnippet
- Diff
- EditResult
- CachedRestore
```

This makes later decisions more precise.

---

# 24. Interaction With Edit Tools

After applying a patch, Litos can know the resulting changed region without rereading.

Example:

```text
Before:
lines 100-110

Patch:
replace lines 104-107

After:
lines 100-112
```

The edit result should include enough surrounding context to establish the new state.

Recommended edit output:

```text
PATCH APPLIED

src/OrderService.cs
Version: B18272

Changed:
104-109

Updated region:
100-113

<small code excerpt>
```

This avoids a reflexive full-file read after every patch.

---

# 25. Tool Description Guidance

Prompting should reinforce the mechanism, but should not be responsible for correctness.

Suggested `read_file` tool description:

> Reads source content while tracking file versions and previously supplied ranges.  
> If the requested content is unchanged and still available in the active context, the tool may return `unchanged` rather than duplicate source text.  
> Treat a returned `unchanged` result as authoritative.  
> Do not repeatedly request the same range unless new information requires it.

Suggested `edit_file` description:

> Successful edits update Litos's known workspace state automatically.  
> Do not reread the edited file solely to confirm that the edit was applied unless the tool reports uncertainty or verification requires additional surrounding context.

---

# 26. Optional Tool-Call Suppression

Litos can go one step further and suppress some duplicate tool calls before they are executed.

Example:

```text
Model requests:
read_file("OrderService.cs", 1, 200)

Tool orchestrator knows:
same file + same version + same range + same context generation

Result:
do not perform physical read
return cached "unchanged"
```

This can reduce:

- Tool execution latency
- Token usage
- Agent step duration

The LLM still receives a normal tool response and does not need special handling.

---

# 27. Avoid Infinite Duplicate Reads

A weak local model may repeatedly issue the same read even after receiving `UNCHANGED`.

Litos should detect this.

Example:

```text
read OrderService.cs
-> unchanged

read OrderService.cs
-> unchanged

read OrderService.cs
-> unchanged
```

After repeated identical calls, return a stronger message:

```text
DUPLICATE_READ_SUPPRESSED

This exact file version and range has already been requested repeatedly.
No new information is available.

Continue using the previously supplied source or choose a different
file/range/tool.
```

Optionally track:

```text
DuplicateReadCount
```

and expose it to an agent governor.

---

# 28. Integrate With a Step Governor

For Litos, this feature can work especially well with a lightweight tool governor.

Example policy:

```text
If same read request occurs twice:
    return UNCHANGED

If same read request occurs three times:
    suppress and advise continuation

If same read request occurs four+ times:
    governor flags reasoning loop
```

This is useful with local models that get stuck in read/verify loops.

---

# 29. Metrics to Collect

To know whether Smart Read is helping, record telemetry locally.

Recommended counters:

```text
read_file_calls
physical_file_reads
unchanged_reads_suppressed
duplicate_bytes_avoided
duplicate_tokens_estimated_avoided
partial_ranges_returned
diffs_returned
cached_ranges_restored_after_compaction
forced_refreshes
duplicate_read_loops_detected
```

Useful task-level summary:

```text
Smart Read statistics

read_file calls:                 84
physical file reads:             29
duplicate reads suppressed:      41
partial reads optimized:          9
diff responses:                   5
estimated source tokens avoided: 63,400
```

This will make the feature measurable rather than anecdotal.

---

# 30. Suggested Configuration

Example:

```json
{
  "smartRead": {
    "enabled": true,
    "hashAlgorithm": "xxHash3",
    "trackLineRanges": true,
    "returnDiffWhenChanged": true,
    "maxDiffRatio": 0.25,
    "trackContextGeneration": true,
    "restoreFromSnapshotAfterCompaction": true,
    "duplicateReadWarningThreshold": 2,
    "duplicateReadSuppressionThreshold": 3,
    "exclude": [
      "**/bin/**",
      "**/obj/**",
      "**/node_modules/**",
      "**/.git/**"
    ]
  }
}
```

---

# 31. Recommended Implementation Phases

## Phase 1 - High-value minimum implementation

Implement:

1. Session-level `FileReadLedger`
2. File content hash
3. Whole-file duplicate detection
4. `UNCHANGED` response
5. Automatic invalidation after edit/write/patch
6. Duplicate-read metrics

This alone should provide substantial savings.

---

## Phase 2 - Range-aware reads

Add:

1. Line-range tracking
2. Range merging
3. Return only unseen portions
4. Track search snippets independently

This will particularly improve large repositories.

---

## Phase 3 - Context-generation awareness

Add:

1. `ContextGeneration`
2. Increment generation during compaction
3. External source snapshot store
4. Restore cached fragments after compaction

This makes the design safe with Litos context compaction.

---

## Phase 4 - Delta reads

Add:

1. Previous-version snapshots
2. File diff generation
3. Diff-versus-full-read threshold
4. Git-assisted diff support

This further reduces repeated source injection.

---

## Phase 5 - Workspace intelligence

Add:

1. `workspace_changes`
2. Workspace checkpoints
3. FileSystemWatcher-based invalidation hints
4. Git operation awareness
5. Agent-loop governor integration

---

# 32. Suggested Internal Components

A possible design:

```text
Litos.Tools.Files
|
+-- SmartReadFileTool
|
+-- FileReadLedger
|
+-- FileVersionService
|   +-- metadata checks
|   +-- xxHash3
|
+-- ReadCoverageTracker
|
+-- WorkspaceSnapshotStore
|
+-- FileDiffService
|
+-- WorkspaceChangeTracker
|
+-- ContextGenerationTracker
|
+-- DuplicateReadGovernor
```

Possible interfaces:

```csharp
public interface IFileVersionService
{
    Task<FileVersion> GetVersionAsync(
        string path,
        CancellationToken cancellationToken);
}

public interface IFileReadLedger
{
    FileReadState? Get(string path);

    void Record(FileReadRecord record);

    void Invalidate(string path);
}

public interface IWorkspaceSnapshotStore
{
    Task StoreAsync(
        string path,
        string version,
        string content,
        CancellationToken cancellationToken);

    Task<string?> TryGetAsync(
        string path,
        string version,
        CancellationToken cancellationToken);
}

public interface IContextGenerationTracker
{
    long CurrentGeneration { get; }

    long AdvanceGeneration();
}
```

---

# 33. Important Design Rule

Do not confuse these concepts:

```text
The file has not changed
```

with:

```text
The model still has the file contents available.
```

They are different.

A correct Smart Read implementation must track both:

```text
Workspace Version
+
Context Visibility
```

This is why `ContextGeneration` is important.

---

# 34. Recommended Final Behavior

The decision tree should look approximately like this:

```text
                         read_file
                             |
                             v
                  Have I seen this path?
                    /                \
                  NO                  YES
                  |                    |
              normal read        same version?
                                  /       \
                                YES       NO
                                 |         |
                    requested range        |
                    already covered?       |
                       /      \             |
                     YES      NO            |
                      |        |             |
           same context gen?   |             |
               /       \       |             |
             YES       NO      |             |
              |         |      |             |
         UNCHANGED    restore  return       DIFF
                      cached   unseen        or READ
                      range    range
```

---

# 35. Why This Fits Litos Well

Litos supports both cloud and local LLM workflows.

Local models are especially sensitive to:

- Long contexts
- Repetitive tool output
- Too many agent steps
- Verification loops
- Tool-call loops
- Context dilution

Smart Read attacks all of these without reducing the correctness of the model's reasoning.

Instead of asking the model:

> "Please remember not to reread things."

Litos guarantees:

> "Repeated reads do not repeatedly consume context unless new information is actually available."

That is a much stronger architecture.

---

# 36. Primary Recommendation

Implement the first version in the **tool execution layer**, not in the prompt.

The first practical release should support:

```text
FileReadLedger
+ xxHash3 file versions
+ whole-file/range coverage
+ compact UNCHANGED responses
+ edit/write invalidation
+ ContextGeneration
```

Then add:

```text
delta reads
workspace_changes
snapshot restoration
duplicate-read governor
```

as follow-up improvements.

The end goal should be:

> **The LLM pays tokens only for source information that is new, changed, newly requested, or no longer available in its active context.**

That is the core principle behind the Smart Read File Tool.
