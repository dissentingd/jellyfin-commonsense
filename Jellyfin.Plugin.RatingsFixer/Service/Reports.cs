using Jellyfin.Plugin.RatingsFixer.Rating;

namespace Jellyfin.Plugin.RatingsFixer.Service;

/// <summary>One item's line in a run report.</summary>
public sealed class ReportEntry
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a series.</summary>
    public bool IsSeries { get; set; }

    /// <summary>Gets or sets the outcome.</summary>
    public DecisionKind Kind { get; set; }

    /// <summary>Gets or sets the current effective rating.</summary>
    public string? Current { get; set; }

    /// <summary>Gets or sets the new rating.</summary>
    public string? New { get; set; }

    /// <summary>Gets or sets the explanation.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets every candidate that was considered.</summary>
    public List<string> Candidates { get; set; } = [];

    /// <summary>Gets or sets how many of a series' seasons and episodes were (or would be) given its rating.</summary>
    public int ChildrenUpdated { get; set; }

    /// <summary>Gets or sets a value indicating whether the change was written.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets the write error, if any.</summary>
    public string? Error { get; set; }
}

/// <summary>What a preview or apply run found and did.</summary>
public sealed class RunReport
{
    /// <summary>Gets or sets a value indicating whether changes were written.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets when the run started (UTC).</summary>
    public DateTime Started { get; set; }

    /// <summary>Gets or sets when the run finished (UTC).</summary>
    public DateTime Finished { get; set; }

    /// <summary>Gets or sets the number of items looked at.</summary>
    public int ItemsScanned { get; set; }

    /// <summary>Gets or sets how many seasons and episodes were (or would be) given their series' rating.</summary>
    public int ChildrenUpdated { get; set; }

    /// <summary>Gets or sets how many of our ratings were re-saved because Jellyfin wasn't enforcing them.</summary>
    public int Repaired { get; set; }

    /// <summary>Gets or sets counts per outcome.</summary>
    public Dictionary<string, int> Counts { get; set; } = [];

    /// <summary>Gets or sets problems that didn't stop the run (a source failing, a bad rule).</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Gets or sets every item not simply kept as-is.</summary>
    public List<ReportEntry> Entries { get; set; } = [];
}

/// <summary>What a revert found and did.</summary>
public sealed class RevertReport
{
    /// <summary>Gets or sets a value indicating whether changes were written.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets how many titles were asked for.</summary>
    public int Requested { get; set; }

    /// <summary>Gets or sets titles reverted (or, in a preview, that would be).</summary>
    public int Reverted { get; set; }

    /// <summary>Gets or sets seasons and episodes reverted with their series.</summary>
    public int ChildrenReverted { get; set; }

    /// <summary>Gets or sets titles added to the exclusion list.</summary>
    public int Excluded { get; set; }

    /// <summary>Gets or sets ids that aren't in the ledger (nothing to revert).</summary>
    public int NotInLedger { get; set; }

    /// <summary>Gets or sets titles whose rating someone changed since the plugin wrote it; left alone.</summary>
    public List<string> ChangedSince { get; set; } = [];

    /// <summary>Gets or sets ledger titles no longer in the library.</summary>
    public List<string> NotFound { get; set; } = [];

    /// <summary>Gets or sets write errors.</summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>What a ledger restore found and did.</summary>
public sealed class RestoreReport
{
    /// <summary>Gets or sets a value indicating whether changes were written.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets the number of ledger entries.</summary>
    public int Entries { get; set; }

    /// <summary>Gets or sets entries whose item was found and already had the rating.</summary>
    public int AlreadyCorrect { get; set; }

    /// <summary>Gets or sets entries whose item was found and (would be) updated.</summary>
    public int Changed { get; set; }

    /// <summary>Gets or sets how many seasons and episodes were (or would be) given their series' rating.</summary>
    public int ChildrenUpdated { get; set; }

    /// <summary>Gets or sets entries with no matching item in the library.</summary>
    public List<string> Unmatched { get; set; } = [];

    /// <summary>Gets or sets write errors.</summary>
    public List<string> Errors { get; set; } = [];
}
