using System.Globalization;
using Litos.Office.Data;
using Litos.Office.Excel;
using Litos.Office.Plan;

namespace Litos.Office.Exec;

public sealed record RunInputs(
    WorkflowPlan Plan,
    ValidationSpec Validation,
    byte[] TemplateBytes,
    TemplateLayout Layout,
    byte[] InputBytes,
    string InputFileName,
    string OutputDir,
    DateTimeOffset Now,
    OfficeLimits? Limits = null);

public enum RunState { Succeeded, Failed }

/// <summary>A run's outcome. <paramref name="OutputPath"/> is set only when the report was
/// published, which only happens when <paramref name="Report"/> has no failures.</summary>
public sealed record RunResult(
    RunState State,
    ValidationReport Report,
    string? OutputPath,
    RowAccounting? Accounting,
    string Summary,
    IReadOnlyList<Finding> Findings)
{
    public bool Succeeded => State == RunState.Succeeded;
}

/// <summary>
/// The one way a learned Excel skill runs (design §2): when testing it on a fresh file, for "Run
/// again", and for a scheduled routine. Order: check the plan → gate and read the input → check the
/// plan against the real columns → execute → check → write in memory → read it back and check again
/// → publish. Any failure stops before publishing; the input is only ever received as bytes, so it
/// can't be changed.
/// </summary>
public static class OfficeWorkflowRunner
{
    public static RunResult Run(RunInputs inputs, CancellationToken ct = default)
    {
        var plan = inputs.Plan;
        var findings = new List<Finding>();

        var planIssues = PlanValidator.Validate(plan);
        if (planIssues.Count > 0)
            return Failed($"The skill's plan isn't valid: {planIssues[0]}", findings);

        LoadedSheet loaded;
        try
        {
            loaded = ReadInput(inputs, findings, ct);
        }
        catch (OfficeFileException ex)
        {
            return Failed(ex.Message, findings);
        }

        var schemaIssues = PlanValidator.Validate(plan, SchemaContext.Of(loaded.Table, plan.Inputs[0].Sheet is null ? null : loaded.Style.SheetName));
        var columnIssues = schemaIssues.Where(i => !i.Path.StartsWith("inputs[0].sheet", StringComparison.Ordinal)).ToList();
        if (columnIssues.Count > 0)
            return Failed(columnIssues[0].Message, findings, loaded.Table.Rows.Count);

        ct.ThrowIfCancellationRequested();
        var result = PlanExecutor.Execute(plan, loaded.Table, ct);
        var report = ValidationRunner.Run(plan, inputs.Validation, loaded.Table, result);
        if (report.HasFailures)
            return new RunResult(RunState.Failed, report, null, result.Accounting, report.FirstFailure!.Message, findings);

        var write = plan.Steps.OfType<WriteWorkbookStep>().Single();
        byte[] bytes;
        Table writtenBack;
        try
        {
            bytes = ExcelWriter.Write(inputs.TemplateBytes, inputs.Layout, write.Sheet, result.Output, result.GrandTotal);
            writtenBack = ExcelReader.Read(bytes, "report.xlsx", write.Sheet, inputs.Limits, ct).Table;
        }
        catch (Exception ex) when (ex is OfficeFileException or IOException or InvalidOperationException or ArgumentException)
        {
            return new RunResult(RunState.Failed, report, null, result.Accounting, $"The report couldn't be written: {ex.Message}", findings);
        }

        report = ValidationRunner.Run(plan, inputs.Validation, loaded.Table, result, writtenBack);
        if (report.HasFailures)
            return new RunResult(RunState.Failed, report, null, result.Accounting, report.FirstFailure!.Message, findings);

        ct.ThrowIfCancellationRequested();
        string path;
        try
        {
            path = OutputPublisher.Publish(bytes, inputs.OutputDir, OutputPublisher.NameFor(plan.Output.FileNamePattern, inputs.Now, inputs.InputFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RunResult(RunState.Failed, report, null, result.Accounting, $"The report couldn't be saved: {ex.Message}", findings);
        }

        return new RunResult(RunState.Succeeded, report, path, result.Accounting, Summarize(plan, result, path), findings);
    }

    /// <summary>The input sheet the plan names; if this file doesn't have it but has exactly one
    /// sheet with data, that one (next month's file is often "Sales Dec", not "Sales Nov").</summary>
    private static LoadedSheet ReadInput(RunInputs inputs, List<Finding> findings, CancellationToken ct)
    {
        var hint = inputs.Plan.Inputs[0].Sheet;
        LoadedSheet loaded;
        try
        {
            loaded = ExcelReader.Read(inputs.InputBytes, inputs.InputFileName, hint, inputs.Limits, ct);
        }
        catch (OfficeFileException ex) when (ex.Code == "sheet_missing")
        {
            loaded = ExcelReader.Read(inputs.InputBytes, inputs.InputFileName, null, inputs.Limits, ct);
            findings.Add(Finding.Warning("sheet_renamed", $"{Path.GetFileName(inputs.InputFileName)} has no sheet \"{hint}\"; the only sheet with data, \"{loaded.Style.SheetName}\", was used."));
        }
        findings.AddRange(loaded.Findings);
        return loaded;
    }

    private static string Summarize(WorkflowPlan plan, ExecResult result, string path)
    {
        var parts = new List<string>
        {
            $"Report written: {Path.GetFileName(path)}",
            $"{result.Output.Rows.Count} row(s)",
            $"{result.Accounting.Included} included, {result.Accounting.Excluded} excluded",
        };
        if (result.GrandTotal is { } total)
        {
            var write = plan.Steps.OfType<WriteWorkbookStep>().Single();
            for (var c = 0; c < total.Length; c++)
                if (total[c].Kind == CellKind.Number)
                    parts.Add($"{write.Columns[c].As} {total[c].Number!.Value.ToString("#,##0.##", CultureInfo.InvariantCulture)}");
        }
        return string.Join("; ", parts);
    }

    private static RunResult Failed(string message, List<Finding> findings, int sourceRows = 0) =>
        new(RunState.Failed,
            new ValidationReport([new ValidationItem(ValidationRunner.Execution, ValidationLevel.Fail, message)]),
            null,
            null,
            message,
            findings);
}
