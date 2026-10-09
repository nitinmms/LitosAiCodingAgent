using Litos.SoftwareFactory.Core.Budget;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>
/// The Budgets and limits tab (blueprint §8.3, m3-architecture.md §5). Its defaults are M2's
/// behaviour, so a factory whose settings were never written runs as it did.
/// </summary>
public sealed record BudgetSettings
{
    /// <summary>The cap a new thread gets when its creator gives none; null for no cap.</summary>
    public long? DefaultTaskBudget { get; init; } = 300_000;

    /// <summary>No thread's cap may be above this, at creation or when raised; null for no limit.</summary>
    public long? MaximumTaskBudget { get; init; }

    /// <summary>Tokens one person's tasks may use in a calendar day (UTC); null for no quota.</summary>
    public long? DailyUserQuota { get; init; }

    /// <summary>Tokens one person's tasks may use in a calendar month (UTC); null for no quota.</summary>
    public long? MonthlyUserQuota { get; init; }

    /// <summary>Verification repair cycles a run may make before it hands off or blocks.</summary>
    public int RepairCyclesPerRun { get; init; } = 2;

    /// <summary>Runs this host executes at once.</summary>
    public int SlotCap { get; init; } = 2;

    /// <summary>A change request adds this share of the task's first cap to its budget.</summary>
    public double ReworkTopUpShare { get; init; } = 0.5;

    /// <summary>The output reserved for, and imposed on, every model call (BudgetPolicy.OutputAllowanceTokens).</summary>
    public int OutputAllowanceTokens { get; init; } = 32_768;

    public const int MaxSlotCap = 16;
    public const int MaxRepairCycles = 10;

    /// <summary>A call needs room for its reply after a reasoning model's thinking; below this it has none.</summary>
    public const int MinOutputAllowance = 4_096;
    public const int MaxOutputAllowance = 200_000;

    public UserQuotas Quotas() => new(DailyUserQuota, MonthlyUserQuota);

    /// <summary>What is wrong with these settings, one sentence each; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DefaultTaskBudget is <= 0)
            errors.Add("The default task budget must be positive, or empty for no cap.");
        if (MaximumTaskBudget is <= 0)
            errors.Add("The maximum task budget must be positive, or empty for no limit.");
        if (MaximumTaskBudget is not null && DefaultTaskBudget is null)
            errors.Add("With a maximum task budget, the default cannot be \"no cap\".");
        if (MaximumTaskBudget is { } maximum and > 0 && DefaultTaskBudget is { } byDefault && byDefault > maximum)
            errors.Add("The default task budget cannot be above the maximum.");
        if (DailyUserQuota is <= 0 || MonthlyUserQuota is <= 0)
            errors.Add("A quota must be positive, or empty for none.");
        if (DailyUserQuota is { } daily && MonthlyUserQuota is { } monthly && daily > monthly)
            errors.Add("The daily quota cannot be above the monthly one.");
        if (RepairCyclesPerRun is < 0 or > MaxRepairCycles)
            errors.Add($"Repair cycles per run must be between 0 and {MaxRepairCycles}.");
        if (SlotCap is < 1 or > MaxSlotCap)
            errors.Add($"Concurrent runs must be between 1 and {MaxSlotCap}.");
        if (ReworkTopUpShare is < 0 or > 10 || double.IsNaN(ReworkTopUpShare))
            errors.Add("A change request's top-up must be between 0 and 10 times the first cap.");
        if (OutputAllowanceTokens is < MinOutputAllowance or > MaxOutputAllowance)
            errors.Add($"The output allowance must be between {Tokens(MinOutputAllowance)} and {Tokens(MaxOutputAllowance)} tokens.");
        return errors;
    }

    /// <summary>Why a thread's cap is refused, or null when it is allowed.</summary>
    public string? RefuseCap(long? cap) => MaximumTaskBudget is not { } maximum
        ? null
        : cap is null
            ? $"A task must have a budget of at most {Tokens(maximum)} tokens."
            : cap > maximum ? $"A task's budget can be at most {Tokens(maximum)} tokens." : null;

    /// <summary>Thousands grouped the same way whatever the host machine's culture (en-IN groups by lakhs).</summary>
    private static string Tokens(long count) => count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
