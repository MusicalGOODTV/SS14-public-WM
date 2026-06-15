

namespace Content.Shared.WeeklyMode;

public enum WeeklySnapshotKind
{
    Auto,
    Manual,
    Endshift,
    RollbackPoint,
    RollbackBackup,
}

public sealed class WeeklyModeSet
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string SetId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BaseMapPrototype { get; set; } = string.Empty;
    public int AutosaveMinutes { get; set; } = 10;
    public int RetainAutosaves { get; set; } = 8;
    public string? CurrentSnapshot { get; set; }
    public List<string> Snapshots { get; set; } = new();
    public List<string> DefaultDisabledJobs { get; set; } = new();
    public Dictionary<string, string> DefaultRoleAliases { get; set; } = new();
    public Dictionary<string, string> CampaignState { get; set; } = new();
}

public sealed class WeeklyModeRuntimeState
{
    public int SchemaVersion { get; set; } = 1;
    public bool IsActive { get; set; }
    public string? ActiveSetId { get; set; }
    public string? ActiveSnapshotId { get; set; }
    public string? PendingSnapshotId { get; set; }
    public int? ActiveWeeklyMapId { get; set; }
    public int? ActiveWeeklyMapEntity { get; set; }
    public List<int> ActiveWeeklyGridIds { get; set; } = new();
    public string? ActiveWeeklyMapPrototype { get; set; }
    public string? ActiveWeeklyMapName { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public string? StartedBy { get; set; }
}

public sealed class WeeklySnapshotMetadata
{
    public int SchemaVersion { get; set; } = WeeklyModeSet.CurrentSchemaVersion;
    public string SnapshotId { get; set; } = string.Empty;
    public string SetId { get; set; } = string.Empty;


    public WeeklySnapshotKind Kind { get; set; }

    public string BaseMapPrototype { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string ContentVersion { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public long BundleSizeBytes { get; set; }
    public bool CompatibleWithBuild { get; set; } = true;
    public int? SavedMapId { get; set; }
    public string SavedMapName { get; set; } = string.Empty;
    public List<int> SavedGridIds { get; set; } = new();
    public int EntityCount { get; set; }
}

public sealed class WeeklyRoleOverrides
{
    public List<string> DisabledJobs { get; set; } = new();
    public Dictionary<string, string> RoleAliases { get; set; } = new();
}

public sealed class WeeklyContainerPatch
{
    public int SchemaVersion { get; set; } = WeeklyModeSet.CurrentSchemaVersion;
    public List<WeeklyContainerEntry> Entries { get; set; } = new();
    public int SkippedExcluded { get; set; }
    public int SkippedUnserialized { get; set; }
}

public sealed class WeeklyContainerEntry
{
    public int OwnerYamlUid { get; set; }
    public string OwnerPrototype { get; set; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public int ChildYamlUid { get; set; }
    public string ChildPrototype { get; set; } = string.Empty;
    public int Index { get; set; }
    public int OwnerDepth { get; set; }
}

public sealed class WeeklySnapshotIntegrity
{
    public int SchemaVersion { get; set; } = WeeklyModeSet.CurrentSchemaVersion;
    public string SnapshotId { get; set; } = string.Empty;
    public string SetId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public Dictionary<string, WeeklySnapshotFileIntegrity> Files { get; set; } = new();
}

public sealed class WeeklySnapshotFileIntegrity
{
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
