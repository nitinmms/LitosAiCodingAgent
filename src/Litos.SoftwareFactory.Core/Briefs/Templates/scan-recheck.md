# Factory decision scan: re-check the remaining questions

A person has answered some of the questions you raised. Before the factory asks the rest, check whether those answers already settle them: a person should not be asked what they have just answered.

{{decisions}}

## Questions not yet asked

{{questions}}

## What to do

Do not read files or run anything: decide from the answers above.

For each question not yet asked, decide whether an answer above already settles it.

- If one does, set `settledBy` to the words of that answer, **quoted exactly**.
- If none does, leave `settledBy` empty.

Do not add new questions, and keep each question's text, category and options as they are.

## Finishing

Call `submit_plan` with `approach` set to `"recheck"`, `files` set to an empty list, and `choices` holding exactly the questions not yet asked, each with the fields `question`, `category`, `options`, `recommendation`, `why` and `settledBy`. From kernel code, pass the arguments as name and value pairs. For example:

```csharp
await submit_plan(
    "approach", "recheck",
    "files", new string[0],
    "choices", new object[] {
        new { question = "Is a partial response kept?", category = "existing-data",
              options = new[] { "Discard it", "Keep it" }, recommendation = "Discard it", why = "",
              settledBy = "nothing partial is shown" } });
```

That call is the only way to finish this turn.
