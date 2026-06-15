using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Content.Server.Chemistry.Components;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Light.EntitySystems;
using Content.Server.Maps;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.Actions;
using Content.Shared.CartridgeLoader;
using Content.Shared.Containers;
using Content.Shared.Containers.ItemSlots;
using Content.Server.WeeklyMode.Storage;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.VendingMachines;
using Content.Shared.WeeklyMode;
using Robust.Server.Containers;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Components;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server.WeeklyMode.Systems;

public sealed class WeeklyModeSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameMapManager _gameMapManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IResourceManager _resource = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly ContainerSystem _containers = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly StationJobsSystem _stationJobs = default!;
    [Dependency] private readonly PoweredLightSystem _poweredLight = default!;
    [Dependency] private readonly LightReplacerSystem _lightReplacer = default!;
    [Dependency] private readonly ActionGrantSystem _actionGrant = default!;
    [Dependency] private readonly BinSystem _bin = default!;

    private readonly WeeklyModeRuntimeState _state = new();
    private readonly Dictionary<EntityUid, Dictionary<string, int?>> _originalSlots = new();
    private readonly Dictionary<string, RollbackConfirmation> _rollbackConfirmations = new();

    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<MetaDataComponent> _metaQuery;
    private EntityQuery<ContainerManagerComponent> _containerQuery;
    private EntityQuery<YamlUidComponent> _yamlUidQuery;
    private EntityQuery<ActorComponent> _actorQuery;
    private EntityQuery<GhostComponent> _ghostQuery;
    private EntityQuery<MindComponent> _mindQuery;
    private EntityQuery<MindContainerComponent> _mindContainerQuery;
    private WeeklyModeStore _store = default!;
    private ISawmill _sawmill = default!;
    private bool _enabled;
    private bool _operationInProgress;
    private TimeSpan? _nextAutosaveAt;
    private MapId? _activeWeeklyMapId;
    private EntityUid? _activeWeeklyMapEntity;
    private readonly List<EntityUid> _activeWeeklyGridIds = new();
    private string? _activeWeeklyMapPrototype;
    private string? _activeWeeklyMapName;

    private sealed record RollbackConfirmation(string SetId, string SnapshotId, DateTime ExpiresAtUtc, string RequestedBy);
    internal readonly record struct SnapshotSanitizationResult(
        int RemovedStationMembers,
        int RemovedSuitSensorEntityReferences,
        int RemovedInvalidContainerReferences,
        int ResetMapInitializationFields,
        int SuppressedMapInitOnlyComponents,
        int SuppressedStartingItems)
    {
        public bool Changed => RemovedStationMembers > 0 ||
                               RemovedSuitSensorEntityReferences > 0 ||
                               RemovedInvalidContainerReferences > 0 ||
                               ResetMapInitializationFields > 0 ||
                               SuppressedMapInitOnlyComponents > 0 ||
                               SuppressedStartingItems > 0;
    }

    private static readonly string[] SnapshotMapInitOnlyComponents =
    {
        "ConditionalSpawner",
        "ContainerFill",
        "EntityTableSpawner",
        "EntityTableContainerFill",
        "RandomDecalSpawner",
        "RandomSpawner",
        "StorageFill",
        "RandomFillSolution",
    };

    public override void Initialize()
    {
        base.Initialize();

        _xformQuery = GetEntityQuery<TransformComponent>();
        _metaQuery = GetEntityQuery<MetaDataComponent>();
        _containerQuery = GetEntityQuery<ContainerManagerComponent>();
        _yamlUidQuery = GetEntityQuery<YamlUidComponent>();
        _actorQuery = GetEntityQuery<ActorComponent>();
        _ghostQuery = GetEntityQuery<GhostComponent>();
        _mindQuery = GetEntityQuery<MindComponent>();
        _mindContainerQuery = GetEntityQuery<MindContainerComponent>();
        _sawmill = _logManager.GetSawmill("weekly-mode");
        RebuildStore(_cfg.GetCVar(CCVars.WeeklyModeDataRoot));

        _enabled = _cfg.GetCVar(CCVars.WeeklyModeEnabled);
        Subs.CVar(_cfg, CCVars.WeeklyModeEnabled, value => _enabled = value, true);
        Subs.CVar(_cfg, CCVars.WeeklyModeDataRoot, RebuildStore, true);

        if (_store.TryLoadState(out var state))
            CopyState(state, _state);

        SubscribeLocalEvent<LoadingMapsEvent>(OnLoadingMaps);
        SubscribeLocalEvent<PreGameMapLoad>(OnPreGameMapLoad);
        SubscribeLocalEvent<PostGameMapLoad>(OnPostGameMapLoad);
        SubscribeLocalEvent<StationInitializedEvent>(OnStationInitialized);
        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
    }

    public override void Update(float frameTime)
    {
        if (!_enabled ||
            !_state.IsActive ||
            _operationInProgress ||
            _state.ActiveSetId == null ||
            _gameTicker.RunLevel != GameRunLevel.InRound ||
            _nextAutosaveAt == null ||
            _timing.CurTime < _nextAutosaveAt.Value)
        {
            return;
        }

        if (!_store.TryLoadSet(_state.ActiveSetId, out var set))
        {
            _sawmill.Error($"Weekly autosave skipped: active set '{_state.ActiveSetId}' is missing.");
            _nextAutosaveAt = _timing.CurTime + TimeSpan.FromMinutes(1);
            return;
        }

        if (!TrySaveSnapshot(set.SetId, WeeklySnapshotKind.Auto, "scheduled autosave", "server", out var message))
            _sawmill.Warning($"Weekly autosave failed: {message}");

        _nextAutosaveAt = _timing.CurTime + TimeSpan.FromMinutes(Math.Max(1, set.AutosaveMinutes));
    }

    public bool TryCreateSet(string setId, string baseMapPrototype, string? displayName, out string message)
    {
        if (!WeeklyModeStore.IsSafeId(setId))
        {
            message = "Invalid setId. Use only ASCII letters, digits, '-', '_' or '.'.";
            return false;
        }

        if (!_prototype.TryIndex<GameMapPrototype>(baseMapPrototype, out _))
        {
            message = $"Unknown map prototype '{baseMapPrototype}'.";
            return false;
        }

        if (_store.TryLoadSet(setId, out _))
        {
            message = $"Weekly set '{setId}' already exists.";
            return false;
        }

        var autosaveMinutes = _cfg.GetCVar(CCVars.WeeklyModeDefaultAutosaveMinutes);
        var retainAutosaves = _cfg.GetCVar(CCVars.WeeklyModeDefaultRetainAutosaves);
        var set = _store.CreateSet(setId, baseMapPrototype, autosaveMinutes, retainAutosaves, displayName);
        message = $"Created weekly set '{set.SetId}' with base map '{set.BaseMapPrototype}'.";
        _sawmill.Info(message);
        return true;
    }

    public string ListSets()
    {
        var ids = _store.ListSetIds().OrderBy(id => id).ToArray();
        if (ids.Length == 0)
            return "No weekly mode sets exist.";

        var lines = new List<string> { "Weekly mode sets:" };
        foreach (var id in ids)
        {
            if (!_store.TryLoadSet(id, out var set))
                continue;

            lines.Add($"- {set.SetId}: base={set.BaseMapPrototype}, current={set.CurrentSnapshot ?? "<base>"}, autosave={set.AutosaveMinutes}m, retain={set.RetainAutosaves}");
        }

        return string.Join('\n', lines);
    }

    public string GetStatus()
    {
        return GetStatus(null);
    }

    public string GetStatus(string? setId)
    {
        if (!_state.IsActive)
        {
            if (setId == null)
                return $"Weekly mode is inactive. Data root: {_store.Root}";

            return DescribeSetStatus(setId);
        }

        var next = _nextAutosaveAt == null
            ? "not scheduled"
            : $"{Math.Max(0, (_nextAutosaveAt.Value - _timing.CurTime).TotalSeconds):F0}s";

        var active = $"Weekly mode active: set={_state.ActiveSetId}, snapshot={_state.ActiveSnapshotId ?? "<base>"}, pending={_state.PendingSnapshotId ?? "<none>"}, next autosave={next}, data root={_store.Root}";
        return setId == null ? active : $"{active}\n{DescribeSetStatus(setId)}";
    }

    private string DescribeSetStatus(string setId)
    {
        if (!_store.TryLoadSet(setId, out var set))
            return $"Weekly set '{setId}' was not found.";

        return $"Set '{set.SetId}': base={set.BaseMapPrototype}, current={set.CurrentSnapshot ?? "<base>"}, disabled=[{string.Join(", ", set.DefaultDisabledJobs)}], aliases={set.DefaultRoleAliases.Count}, snapshots={set.Snapshots.Count}.";
    }

    public string ListSnapshots(string setId)
    {
        if (!_store.TryLoadSet(setId, out var set))
            return $"Weekly set '{setId}' was not found.";

        if (set.Snapshots.Count == 0)
            return $"Weekly set '{set.SetId}' has no snapshots.";

        var now = DateTime.UtcNow;
        var contentVersion = _cfg.GetCVar(Robust.Shared.CVars.BuildVersion);
        var engineVersion = _cfg.GetCVar(Robust.Shared.CVars.BuildEngineVersion);
        var lines = new List<string> { $"Snapshots for weekly set '{set.SetId}':" };

        foreach (var snapshotId in set.Snapshots.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!_store.TryLoadSnapshot(set.SetId, snapshotId, out var metadata))
            {
                lines.Add($"- {snapshotId}: unreadable or incomplete");
                continue;
            }

            var compatible = metadata.SchemaVersion == WeeklyModeSet.CurrentSchemaVersion &&
                             metadata.ContentVersion == contentVersion &&
                             metadata.EngineVersion == engineVersion;
            metadata.CompatibleWithBuild = compatible;
            var age = now - metadata.CreatedAtUtc;
            var size = metadata.BundleSizeBytes > 0
                ? metadata.BundleSizeBytes
                : _store.GetDirectorySize(_store.SnapshotDirectory(set.SetId, snapshotId));
            var current = snapshotId == set.CurrentSnapshot ? " current" : string.Empty;
            var savedMapName = string.IsNullOrWhiteSpace(metadata.SavedMapName)
                ? "<unknown>"
                : metadata.SavedMapName;
            var savedMapId = metadata.SavedMapId?.ToString(CultureInfo.InvariantCulture) ?? "<unknown>";
            lines.Add($"- {snapshotId}{current}: kind={metadata.Kind}, utc={metadata.CreatedAtUtc:O}, age={FormatAge(age)}, size={size}B, compatible={compatible}, map={savedMapName}, mapId={savedMapId}, entities={metadata.EntityCount}, by={metadata.CreatedBy}, note=\"{metadata.Notes}\"");
        }

        return string.Join('\n', lines);
    }

    public bool TryDeleteSnapshot(string setId, string snapshotId, out string message)
    {
        if (!TryLoadMutableSet(setId, out var set, out message))
            return false;

        if (!WeeklyModeStore.IsSafeId(snapshotId))
        {
            message = "Invalid snapshotId. Use only ASCII letters, digits, '-', '_' or '.'.";
            return false;
        }

        if (set.CurrentSnapshot == snapshotId)
        {
            message = $"Snapshot '{snapshotId}' is current for set '{set.SetId}' and cannot be deleted.";
            return false;
        }

        if (!_store.SnapshotExists(set.SetId, snapshotId) && !set.Snapshots.Contains(snapshotId))
        {
            message = $"Snapshot '{snapshotId}' was not found in set '{set.SetId}'.";
            return false;
        }

        _store.DeleteSnapshot(set.SetId, snapshotId);
        set.Snapshots.Remove(snapshotId);
        _store.SaveSet(set);
        message = $"Deleted snapshot '{snapshotId}' from set '{set.SetId}'.";
        _sawmill.Warning(message);
        return true;
    }

    public bool TryStart(string setId, string? snapshotId, string startedBy, out string message)
    {
        if (!EnsureEnabled(out message))
            return false;

        if (!TryEnterOperation(out message))
            return false;

        try
        {
            if (!_store.TryLoadSet(setId, out var set))
            {
                message = $"Weekly set '{setId}' was not found.";
                return false;
            }

            var selectedSnapshot = snapshotId ?? set.CurrentSnapshot;
            if (selectedSnapshot != null &&
                !TryLoadCompatibleSnapshot(set, selectedSnapshot, out _, out message))
            {
                return false;
            }

            _state.IsActive = true;
            _state.ActiveSetId = set.SetId;
            _state.PendingSnapshotId = selectedSnapshot;
            _state.ActiveSnapshotId = selectedSnapshot;
            _state.StartedAtUtc = DateTime.UtcNow;
            _state.StartedBy = startedBy;
            ClearActiveWeeklyMapTracking();
            _store.SaveState(_state);

            RestartForWeeklyMap();

            message = selectedSnapshot == null
                ? $"Weekly set '{set.SetId}' armed. The next round will start from base map '{set.BaseMapPrototype}'."
                : $"Weekly set '{set.SetId}' armed. The next round will start from snapshot '{selectedSnapshot}'.";
            _sawmill.Info($"{message} Operator: {startedBy}");
            return true;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    public bool TryRollback(string setId, string snapshotId, string startedBy, out string message)
    {
        if (!EnsureEnabled(out message))
            return false;

        if (!WeeklyModeStore.IsSafeId(snapshotId))
        {
            message = "Invalid snapshotId. Use only ASCII letters, digits, '-', '_' or '.'.";
            return false;
        }

        if (!TryEnterOperation(out message))
            return false;

        try
        {
            if (!_store.TryLoadSet(setId, out var set))
            {
                message = $"Weekly set '{setId}' was not found.";
                return false;
            }

            if (!TryLoadCompatibleSnapshot(set, snapshotId, out var targetMetadata, out message))
                return false;

            if (targetMetadata.SchemaVersion != WeeklyModeSet.CurrentSchemaVersion)
            {
                message = $"Snapshot '{snapshotId}' schemaVersion {targetMetadata.SchemaVersion} is not compatible with weekly schema {WeeklyModeSet.CurrentSchemaVersion}.";
                return false;
            }

            if (!TrySaveSnapshotInternal(
                    set.SetId,
                    WeeklySnapshotKind.RollbackBackup,
                    $"rollback backup before restoring {snapshotId}",
                    startedBy,
                    false,
                    out var backupId,
                    out var backupMessage))
            {
                message = $"Rollback aborted because backup failed: {backupMessage}";
                return false;
            }

            set.CurrentSnapshot = snapshotId;
            _store.SaveSet(set);

            _state.IsActive = true;
            _state.ActiveSetId = set.SetId;
            _state.PendingSnapshotId = snapshotId;
            _state.ActiveSnapshotId = snapshotId;
            _state.StartedAtUtc ??= DateTime.UtcNow;
            _state.StartedBy = startedBy;
            ClearActiveWeeklyMapTracking();
            _store.SaveState(_state);

            RestartForWeeklyMap();

            _chatManager.DispatchServerAnnouncement($"Weekly Mode rollback is restarting the round into snapshot '{snapshotId}'. Backup snapshot: '{backupId}'.");

            message = $"Weekly rollback armed for set '{set.SetId}' snapshot '{snapshotId}'. Backup '{backupId}' was created and the round is restarting into the selected snapshot.";
            _sawmill.Warning($"{message} Operator: {startedBy}");
            return true;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    public bool TryPrepareRollback(string setId, string snapshotId, string requestedBy, out string token, out string message)
    {
        token = string.Empty;
        if (!EnsureEnabled(out message))
            return false;

        if (!WeeklyModeStore.IsSafeId(snapshotId))
        {
            message = "Invalid snapshotId. Use only ASCII letters, digits, '-', '_' or '.'.";
            return false;
        }

        if (!_store.TryLoadSet(setId, out var set))
        {
            message = $"Weekly set '{setId}' was not found.";
            return false;
        }

        if (!TryLoadCompatibleSnapshot(set, snapshotId, out var metadata, out message))
            return false;

        token = Guid.NewGuid().ToString("N")[..8];
        var expires = DateTime.UtcNow.AddMinutes(2);
        _rollbackConfirmations[token] = new RollbackConfirmation(set.SetId, snapshotId, expires, requestedBy);
        message = $"Rollback confirmation token: {token}\nSnapshot: {metadata.SnapshotId} ({metadata.Kind}, {metadata.CreatedAtUtc:O}, by {metadata.CreatedBy})\nThis will restart the round. Confirm within 2 minutes with: wm.rollback {set.SetId} {snapshotId} {token}";
        _sawmill.Warning($"Rollback token {token} prepared for set '{set.SetId}' snapshot '{snapshotId}' by {requestedBy}.");
        return true;
    }

    public bool TryConfirmRollback(string setId, string snapshotId, string token, string startedBy, out string message)
    {
        if (!_rollbackConfirmations.TryGetValue(token, out var confirmation) ||
            confirmation.SetId != setId ||
            confirmation.SnapshotId != snapshotId)
        {
            message = "Invalid rollback confirmation token.";
            return false;
        }

        if (confirmation.ExpiresAtUtc < DateTime.UtcNow)
        {
            _rollbackConfirmations.Remove(token);
            message = "Rollback confirmation token has expired.";
            return false;
        }

        _rollbackConfirmations.Remove(token);
        return TryRollback(setId, snapshotId, startedBy, out message);
    }

    public bool TryCancel(out string message)
    {
        if (!TryEnterOperation(out message))
            return false;

        try
        {
            _state.IsActive = false;
            _state.ActiveSetId = null;
            _state.ActiveSnapshotId = null;
            _state.PendingSnapshotId = null;
            _state.StartedAtUtc = null;
            _state.StartedBy = null;
            ClearActiveWeeklyMapTracking();
            _nextAutosaveAt = null;
            _originalSlots.Clear();
            _store.SaveState(_state);
            message = "Weekly mode cancelled. Future rounds will use normal map selection.";
            _sawmill.Info(message);
            return true;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    public bool TrySaveSnapshot(string setId, WeeklySnapshotKind kind, string notes, string createdBy, out string message)
    {
        if (!EnsureEnabled(out message))
            return false;

        if (!TryEnterOperation(out message))
            return false;

        try
        {
            return TrySaveSnapshotInternal(setId, kind, notes, createdBy, true, out _, out message);
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private bool TrySaveSnapshotInternal(
        string setId,
        WeeklySnapshotKind kind,
        string notes,
        string createdBy,
        bool makeCurrent,
        [NotNullWhen(true)] out string? snapshotId,
        out string message)
    {
        snapshotId = null;

        if (!_store.TryLoadSet(setId, out var set))
        {
            message = $"Weekly set '{setId}' was not found.";
            return false;
        }

        if (!IsActiveSet(set.SetId))
        {
            message = $"Weekly set '{set.SetId}' is not the active weekly set.";
            return false;
        }

        if (!TryResolveActiveWeeklyMap(
                set,
                out var mapId,
                out var mapEntity,
                out var mapName,
                out var gridUids,
                out var activeEntityCount,
                out message))
        {
            return false;
        }

        _sawmill.Info(
            "Weekly save:\n" +
            $"set={set.SetId}\n" +
            $"activeMapId={(int) mapId}\n" +
            $"activeMapEntity={mapEntity.Id}\n" +
            $"activeMapName={mapName}\n" +
            $"baseMapPrototype={set.BaseMapPrototype}\n" +
            $"grids={string.Join(",", gridUids.Select(uid => uid.Id))}\n" +
            $"entities={activeEntityCount}");

        snapshotId = BuildSnapshotId(kind);
        var tempDirectory = _store.TempSnapshotDirectory(set.SetId, snapshotId);
        var stationPath = _store.SnapshotFilePath(tempDirectory, WeeklyModeStore.StationFileName);

        _resource.UserData.Delete(tempDirectory);
        _resource.UserData.CreateDir(tempDirectory);

        var excludedRoots = CollectSnapshotExcludedRoots();
        if (!TrySaveMapExcludingPlayers(mapId, stationPath, excludedRoots, out var yamlUidMap))
        {
            _resource.UserData.Delete(tempDirectory);
            message = $"Failed to serialize map {mapId} for snapshot '{snapshotId}'.";
            return false;
        }

        if (!TrySanitizeSerializedSnapshot(stationPath, out var sanitization, out message))
        {
            _resource.UserData.Delete(tempDirectory);
            return false;
        }

        if (sanitization.Changed)
        {
            _sawmill.Info(
                "Weekly snapshot sanitation:\n" +
                $"snapshot={snapshotId}\n" +
                $"removedStationMembers={sanitization.RemovedStationMembers}\n" +
                $"removedSuitSensorEntityReferences={sanitization.RemovedSuitSensorEntityReferences}\n" +
                $"removedInvalidContainerReferences={sanitization.RemovedInvalidContainerReferences}\n" +
                $"resetMapInitializationFields={sanitization.ResetMapInitializationFields}\n" +
                $"suppressedMapInitOnlyComponents={sanitization.SuppressedMapInitOnlyComponents}\n" +
                $"suppressedStartingItems={sanitization.SuppressedStartingItems}");
        }

        var patch = BuildContainerPatch(yamlUidMap, excludedRoots);
        var roleOverrides = new WeeklyRoleOverrides
        {
            DisabledJobs = set.DefaultDisabledJobs.ToList(),
            RoleAliases = new Dictionary<string, string>(set.DefaultRoleAliases),
        };

        var metadata = new WeeklySnapshotMetadata
        {
            SnapshotId = snapshotId,
            SetId = set.SetId,
            Kind = kind,
            BaseMapPrototype = set.BaseMapPrototype,
            CreatedAtUtc = DateTime.UtcNow,
            ContentVersion = _cfg.GetCVar(Robust.Shared.CVars.BuildVersion),
            EngineVersion = _cfg.GetCVar(Robust.Shared.CVars.BuildEngineVersion),
            Notes = notes,
            CreatedBy = createdBy,
            SavedMapId = (int) mapId,
            SavedMapName = mapName,
            SavedGridIds = gridUids.Select(uid => uid.Id).OrderBy(id => id).ToList(),
            EntityCount = yamlUidMap.Count,
        };

        _store.SaveRoleOverrides(tempDirectory, roleOverrides);
        _store.SaveContainerPatch(tempDirectory, patch);
        _store.SaveSnapshotMetadata(tempDirectory, metadata);

        metadata.BundleSizeBytes = _store.GetDirectorySize(tempDirectory);
        _store.SaveSnapshotMetadata(tempDirectory, metadata);

        var integrity = BuildIntegrity(set.SetId, snapshotId, metadata.CreatedAtUtc, tempDirectory);
        _store.SaveSnapshotIntegrity(tempDirectory, integrity);

        if (!IsCompleteSnapshotDirectory(tempDirectory, out message))
        {
            _resource.UserData.Delete(tempDirectory);
            return false;
        }

        metadata.BundleSizeBytes = _store.GetDirectorySize(tempDirectory);
        _store.SaveSnapshotMetadata(tempDirectory, metadata);
        integrity = BuildIntegrity(set.SetId, snapshotId, metadata.CreatedAtUtc, tempDirectory);
        _store.SaveSnapshotIntegrity(tempDirectory, integrity);

        _store.ReplaceSnapshotDirectory(tempDirectory, set.SetId, snapshotId);

        if (!set.Snapshots.Contains(snapshotId))
            set.Snapshots.Add(snapshotId);

        if (makeCurrent)
        {
            set.CurrentSnapshot = snapshotId;
            _state.ActiveSnapshotId = snapshotId;
            _store.SaveState(_state);
        }

        PruneAutosaves(set);
        _store.SaveSet(set);

        message = $"Saved weekly snapshot '{snapshotId}' for set '{set.SetId}' to {_store.SnapshotDirectory(set.SetId, snapshotId)}.";
        _sawmill.Info($"{message} Operator: {createdBy}");
        return true;
    }

    public bool TryDisableRoles(string setId, IReadOnlyList<string> jobIds, out string message)
    {
        if (!TryLoadMutableSet(setId, out var set, out message))
            return false;

        if (jobIds.Count is < 1 or > 10)
        {
            message = "Disable role batch must contain between 1 and 10 job IDs.";
            return false;
        }

        foreach (var jobId in jobIds)
        {
            if (!_prototype.TryIndex<JobPrototype>(jobId, out _))
            {
                message = $"Unknown job prototype '{jobId}'. No roles were changed.";
                return false;
            }
        }

        var added = new List<string>();
        foreach (var jobId in jobIds.Distinct(StringComparer.Ordinal))
        {
            if (set.DefaultDisabledJobs.Contains(jobId))
                continue;

            set.DefaultDisabledJobs.Add(jobId);
            added.Add(jobId);
        }

        set.DefaultDisabledJobs.Sort(StringComparer.Ordinal);
        _store.SaveSet(set);

        if (IsActiveSet(set.SetId))
            ApplyRoleOverridesToStations(set);

        message = added.Count == 0
            ? $"No role changes were needed for set '{set.SetId}'."
            : $"Disabled roles for set '{set.SetId}': {string.Join(", ", added)}.";
        return true;
    }

    public bool TryEnableRoles(string setId, IReadOnlyList<string> jobIds, out string message)
    {
        if (!TryLoadMutableSet(setId, out var set, out message))
            return false;

        var removed = new List<string>();
        foreach (var jobId in jobIds)
        {
            if (set.DefaultDisabledJobs.Remove(jobId))
                removed.Add(jobId);
        }

        _store.SaveSet(set);

        if (IsActiveSet(set.SetId))
            RestoreRolesOnStations(removed);

        message = removed.Count == 0
            ? $"No role changes were needed for set '{set.SetId}'."
            : $"Enabled roles for set '{set.SetId}': {string.Join(", ", removed)}.";
        return true;
    }

    public bool TryRenameRole(string setId, string jobId, string alias, out string message)
    {
        if (!TryLoadMutableSet(setId, out var set, out message))
            return false;

        if (!_prototype.TryIndex<JobPrototype>(jobId, out _))
        {
            message = $"Unknown job prototype '{jobId}'.";
            return false;
        }

        if (!TryNormalizeAlias(alias, out alias, out message))
            return false;

        set.DefaultRoleAliases.TryGetValue(jobId, out var oldAlias);
        set.DefaultRoleAliases[jobId] = alias;
        _store.SaveSet(set);

        message = $"Renamed role '{jobId}' to '{alias}' for weekly set '{set.SetId}'.";
        _sawmill.Info($"Role alias changed for set '{set.SetId}' job '{jobId}': old='{oldAlias ?? "<none>"}', new='{alias}'.");
        return true;
    }

    public bool TryRenameRolesBatch(string setId, string batch, out string message)
    {
        if (!TryLoadMutableSet(setId, out var set, out message))
            return false;

        var parsed = new List<(string JobId, string Alias)>();
        foreach (var entry in batch.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                message = $"Invalid alias entry '{entry}'. Use jobId=alias;jobId=alias.";
                return false;
            }

            var jobId = entry[..separator].Trim();
            var alias = entry[(separator + 1)..].Trim().Trim('"');

            if (!_prototype.TryIndex<JobPrototype>(jobId, out _))
            {
                message = $"Unknown job prototype '{jobId}'.";
                return false;
            }

            if (!TryNormalizeAlias(alias, out alias, out message))
                return false;

            parsed.Add((jobId, alias));
        }

        var changed = new List<string>();
        foreach (var (jobId, alias) in parsed)
        {
            set.DefaultRoleAliases.TryGetValue(jobId, out var oldAlias);
            set.DefaultRoleAliases[jobId] = alias;
            changed.Add(jobId);
            _sawmill.Info($"Role alias changed for set '{set.SetId}' job '{jobId}': old='{oldAlias ?? "<none>"}', new='{alias}'.");
        }

        _store.SaveSet(set);
        message = changed.Count == 0
            ? $"No aliases were changed for set '{set.SetId}'."
            : $"Updated aliases for set '{set.SetId}': {string.Join(", ", changed)}.";
        return true;
    }

    public string ListRoleAliases(string setId)
    {
        if (!_store.TryLoadSet(setId, out var set))
            return $"Weekly set '{setId}' was not found.";

        if (set.DefaultRoleAliases.Count == 0)
            return $"Weekly set '{set.SetId}' has no role aliases.";

        var lines = new List<string> { $"Role aliases for weekly set '{set.SetId}':" };
        foreach (var (jobId, alias) in set.DefaultRoleAliases.OrderBy(x => x.Key, StringComparer.Ordinal))
            lines.Add($"- {jobId}: {alias}");

        return string.Join('\n', lines);
    }

    public bool TryClearRoleAliases(string setId, IReadOnlyList<string> jobIds, out string message)
    {
        if (!TryLoadMutableSet(setId, out var set, out message))
            return false;

        if (jobIds.Count > 0)
        {
            foreach (var jobId in jobIds)
            {
                if (!_prototype.TryIndex<JobPrototype>(jobId, out _))
                {
                    message = $"Unknown job prototype '{jobId}'. No aliases were changed.";
                    return false;
                }
            }
        }

        var removed = new List<string>();
        if (jobIds.Count == 0)
        {
            removed.AddRange(set.DefaultRoleAliases.Keys);
            set.DefaultRoleAliases.Clear();
        }
        else
        {
            foreach (var jobId in jobIds.Distinct(StringComparer.Ordinal))
            {
                if (set.DefaultRoleAliases.Remove(jobId))
                    removed.Add(jobId);
            }
        }

        _store.SaveSet(set);
        message = removed.Count == 0
            ? $"No aliases were cleared for set '{set.SetId}'."
            : $"Cleared aliases for set '{set.SetId}': {string.Join(", ", removed)}.";
        _sawmill.Info(message);
        return true;
    }

    public bool TryClearRoles(string setId, out string message)
    {
        if (!TryLoadMutableSet(setId, out var set, out message))
            return false;

        var restored = set.DefaultDisabledJobs.ToList();
        set.DefaultDisabledJobs.Clear();
        set.DefaultRoleAliases.Clear();
        _store.SaveSet(set);

        if (IsActiveSet(set.SetId))
            RestoreRolesOnStations(restored);

        message = $"Cleared disabled roles and aliases for set '{set.SetId}'.";
        return true;
    }

    public bool TryGetRoleAlias(string jobId, [NotNullWhen(true)] out string? alias)
    {
        alias = null;
        if (!_state.IsActive || _state.ActiveSetId == null)
            return false;

        if (!_store.TryLoadSet(_state.ActiveSetId, out var set))
            return false;

        return set.DefaultRoleAliases.TryGetValue(jobId, out alias) && !string.IsNullOrWhiteSpace(alias);
    }

    public string GetRoleDisplayName(string jobId, string fallback)
    {
        return TryGetRoleAlias(jobId, out var alias) ? alias : fallback;
    }

    public string GetJobDisplayName(ProtoId<JobPrototype> jobId)
    {
        if (_prototype.TryIndex<JobPrototype>(jobId, out var job))
            return GetRoleDisplayName(job.ID, job.LocalizedName);

        return jobId.Id;
    }

    public Dictionary<ProtoId<JobPrototype>, string> GetActiveRoleAliases()
    {
        if (!_enabled || !_state.IsActive || _state.ActiveSetId == null)
            return new();

        if (!_store.TryLoadSet(_state.ActiveSetId, out var set))
            return new();

        var aliases = new Dictionary<ProtoId<JobPrototype>, string>();
        foreach (var (jobId, alias) in set.DefaultRoleAliases)
        {
            if (_prototype.HasIndex<JobPrototype>(jobId))
                aliases[new ProtoId<JobPrototype>(jobId)] = alias;
        }

        return aliases;
    }

    private void OnLoadingMaps(LoadingMapsEvent ev)
    {
        if (!_enabled || !_state.IsActive || _state.ActiveSetId == null)
            return;

        if (!_store.TryLoadSet(_state.ActiveSetId, out var set))
        {
            _sawmill.Error($"Weekly map selection failed: set '{_state.ActiveSetId}' is missing.");
            throw new InvalidOperationException($"Weekly map selection failed: set '{_state.ActiveSetId}' is missing.");
        }

        ClearActiveWeeklyMapTracking();
        var previousMap = GetCurrentOrConfiguredMapName();
        var snapshotId = _state.PendingSnapshotId ?? set.CurrentSnapshot;
        if (snapshotId != null)
        {
            if (!TryLoadCompatibleSnapshot(set, snapshotId, out _, out var validationMessage))
                throw new InvalidOperationException($"Weekly snapshot selection failed: {validationMessage}");

            var stationPath = _store.SnapshotStationPath(set.SetId, snapshotId);
            _gameMapManager.SelectPersistentMap(set.BaseMapPrototype, stationPath);
            _state.ActiveSnapshotId = snapshotId;
            _state.PendingSnapshotId = null;
            _store.SaveState(_state);
            _sawmill.Info(
                "Weekly snapshot start:\n" +
                $"set={set.SetId}\n" +
                $"snapshot={snapshotId}\n" +
                $"stationPath={stationPath}\n" +
                $"previousMap={previousMap}");
        }
        else
        {
            _gameMapManager.SelectMap(set.BaseMapPrototype);
            _state.ActiveSnapshotId = null;
            _state.PendingSnapshotId = null;
            _store.SaveState(_state);
            _sawmill.Info(
                "Weekly first start:\n" +
                $"set={set.SetId}\n" +
                $"baseMapPrototype={set.BaseMapPrototype}\n" +
                "source=BaseMap\n" +
                $"previousMap={previousMap}");
        }

        var selected = _gameMapManager.GetSelectedMap();
        if (selected == null)
            throw new InvalidOperationException($"Weekly map selection failed: base map '{set.BaseMapPrototype}' could not be selected.");

        if (!string.Equals(selected.ID, set.BaseMapPrototype, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Weekly map selection failed: selected map does not match the map assigned to this Weekly set.\n" +
                $"Expected: {set.BaseMapPrototype}\n" +
                $"Actual: {selected.ID}");
        }

        ev.Maps.Clear();
        ev.Maps.Add(selected);
    }

    private void OnPreGameMapLoad(PreGameMapLoad ev)
    {
        if (!_enabled ||
            !_state.IsActive ||
            _state.ActiveSetId == null ||
            (_state.PendingSnapshotId ?? _state.ActiveSnapshotId) == null)
        {
            return;
        }

        ev.Options = ev.Options with { StoreYamlUids = true };
    }

    private void OnPostGameMapLoad(PostGameMapLoad ev)
    {
        if (!_enabled || !_state.IsActive || _state.ActiveSetId == null)
            return;

        if (!_store.TryLoadSet(_state.ActiveSetId, out var set))
            return;

        var (mapEntity, gridUids) = TrackActiveWeeklyMap(set, ev);

        if (_state.ActiveSnapshotId == null)
            return;

        SuppressSnapshotMapInitOnlyComponents(ev.Map, mapEntity, gridUids);
        SuppressSnapshotStartingItems(ev.Map, mapEntity, gridUids);

        if (!_store.TryLoadContainerPatch(_state.ActiveSetId, _state.ActiveSnapshotId, out var patch))
        {
            _sawmill.Warning($"Weekly snapshot '{_state.ActiveSnapshotId}' has no readable container patch.");
            return;
        }

        ApplyContainerPatch(ev.Map, mapEntity, gridUids, patch, _state.ActiveSetId, _state.ActiveSnapshotId);
    }

    private void OnStationInitialized(StationInitializedEvent ev)
    {
        if (!_enabled || !_state.IsActive || _state.ActiveSetId == null)
            return;

        if (!_store.TryLoadSet(_state.ActiveSetId, out var set))
            return;

        ApplyRoleOverridesToStation(ev.Station, set);
    }

    private void OnRoundStarted(RoundStartedEvent ev)
    {
        if (!_enabled || !_state.IsActive || _state.ActiveSetId == null)
        {
            _nextAutosaveAt = null;
            return;
        }

        if (!_store.TryLoadSet(_state.ActiveSetId, out var set))
        {
            _nextAutosaveAt = null;
            return;
        }

        _nextAutosaveAt = _timing.CurTime + TimeSpan.FromMinutes(Math.Max(1, set.AutosaveMinutes));
    }

    private (EntityUid MapEntity, List<EntityUid> GridUids) TrackActiveWeeklyMap(WeeklyModeSet set, PostGameMapLoad ev)
    {
        if (!string.Equals(ev.GameMap.ID, set.BaseMapPrototype, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Weekly map load failed: loaded map does not match the map assigned to this Weekly set.\n" +
                $"Expected: {set.BaseMapPrototype}\n" +
                $"Actual: {ev.GameMap.ID}");
        }

        var mapEntity = _map.GetMapOrInvalid(ev.Map);
        if (!mapEntity.IsValid() ||
            !_metaQuery.TryGetComponent(mapEntity, out var meta) ||
            meta.EntityLifeStage >= EntityLifeStage.Terminating)
        {
            throw new InvalidOperationException($"Weekly map load failed: loaded map {ev.Map} has no live map entity.");
        }

        var mapName = string.IsNullOrWhiteSpace(meta.EntityName)
            ? ev.GameMap.MapName
            : meta.EntityName;

        if (IsDevMapMismatch(set.BaseMapPrototype, mapName))
        {
            throw new InvalidOperationException(
                "Weekly map load failed: active map does not match the map assigned to this Weekly set.\n" +
                $"Expected: {set.BaseMapPrototype}\n" +
                $"Actual: {mapName}");
        }

        var gridUids = CollectGridUids(ev.Map);
        foreach (var uid in ev.Grids)
        {
            if (uid.IsValid() && !gridUids.Contains(uid))
                gridUids.Add(uid);
        }

        _activeWeeklyMapId = ev.Map;
        _activeWeeklyMapEntity = mapEntity;
        _activeWeeklyGridIds.Clear();
        _activeWeeklyGridIds.AddRange(gridUids);
        _activeWeeklyMapPrototype = set.BaseMapPrototype;
        _activeWeeklyMapName = mapName;

        _state.ActiveWeeklyMapId = (int) ev.Map;
        _state.ActiveWeeklyMapEntity = mapEntity.Id;
        _state.ActiveWeeklyGridIds = gridUids.Select(uid => uid.Id).OrderBy(id => id).ToList();
        _state.ActiveWeeklyMapPrototype = set.BaseMapPrototype;
        _state.ActiveWeeklyMapName = mapName;
        _store.SaveState(_state);

        var entityCount = CountEntitiesOnMap(ev.Map, mapEntity, gridUids);
        var prefix = _state.ActiveSnapshotId == null
            ? "Weekly first start loaded:"
            : "Weekly snapshot loaded:";

        _sawmill.Info(
            $"{prefix}\n" +
            $"newMapId={(int) ev.Map}\n" +
            $"mapEntity={mapEntity.Id}\n" +
                $"mapName={mapName}\n" +
                $"grids={string.Join(",", gridUids.Select(uid => uid.Id))}\n" +
                $"entities={entityCount}");

        return (mapEntity, gridUids);
    }

    private bool TryResolveActiveWeeklyMap(
        WeeklyModeSet set,
        out MapId mapId,
        out EntityUid mapEntity,
        out string mapName,
        out List<EntityUid> gridUids,
        out int entityCount,
        out string message)
    {
        mapId = MapId.Nullspace;
        mapEntity = EntityUid.Invalid;
        mapName = string.Empty;
        gridUids = new List<EntityUid>();
        entityCount = 0;

        if (_activeWeeklyMapId == null && _state.ActiveWeeklyMapId is { } persistedMapId)
            _activeWeeklyMapId = new MapId(persistedMapId);

        if (_activeWeeklyMapId == null || _activeWeeklyMapId.Value == MapId.Nullspace)
        {
            message = "Weekly save aborted: no active Weekly map has been loaded for this round.";
            return false;
        }

        mapId = _activeWeeklyMapId.Value;
        if (!_map.MapExists(mapId))
        {
            message = $"Weekly save aborted: active Weekly map {mapId} no longer exists.";
            return false;
        }

        mapEntity = _activeWeeklyMapEntity ?? _map.GetMapOrInvalid(mapId);
        if (!mapEntity.IsValid() ||
            !_metaQuery.TryGetComponent(mapEntity, out var meta) ||
            meta.EntityLifeStage >= EntityLifeStage.Terminating)
        {
            message = $"Weekly save aborted: active Weekly map {mapId} is terminating or has no live map entity.";
            return false;
        }

        var activePrototype = _activeWeeklyMapPrototype ?? _state.ActiveWeeklyMapPrototype;
        if (!string.Equals(activePrototype, set.BaseMapPrototype, StringComparison.Ordinal))
        {
            message =
                "Weekly save aborted:\n" +
                "active map does not match the map assigned to this Weekly set.\n" +
                $"Expected: {set.BaseMapPrototype}\n" +
                $"Actual: {activePrototype ?? "<unknown>"}";
            return false;
        }

        mapName = string.IsNullOrWhiteSpace(meta.EntityName)
            ? _activeWeeklyMapName ?? _state.ActiveWeeklyMapName ?? mapId.ToString()
            : meta.EntityName;

        if (IsDevMapMismatch(set.BaseMapPrototype, mapName))
        {
            message =
                "Weekly save aborted:\n" +
                "active map does not match the map assigned to this Weekly set.\n" +
                $"Expected: {set.BaseMapPrototype}\n" +
                $"Actual: {mapName}";
            return false;
        }

        gridUids = CollectGridUids(mapId);
        if (gridUids.Count == 0 && _activeWeeklyGridIds.Count > 0)
            gridUids.AddRange(_activeWeeklyGridIds.Where(uid => uid.IsValid()));

        entityCount = CountEntitiesOnMap(mapId, mapEntity, gridUids);
        message = string.Empty;
        return true;
    }

    private bool TryLoadCompatibleSnapshot(
        WeeklyModeSet set,
        string snapshotId,
        [NotNullWhen(true)] out WeeklySnapshotMetadata? metadata,
        out string message)
    {
        metadata = null;

        if (!_store.TryLoadSnapshot(set.SetId, snapshotId, out metadata))
        {
            message = $"Snapshot '{snapshotId}' was not found in set '{set.SetId}'.";
            return false;
        }

        if (!string.Equals(metadata.SetId, set.SetId, StringComparison.Ordinal))
        {
            message = $"Snapshot '{snapshotId}' belongs to set '{metadata.SetId}', not '{set.SetId}'.";
            return false;
        }

        if (!string.Equals(metadata.BaseMapPrototype, set.BaseMapPrototype, StringComparison.Ordinal))
        {
            message =
                "Snapshot map mismatch:\n" +
                $"set base map: {set.BaseMapPrototype}\n" +
                $"snapshot map: {metadata.BaseMapPrototype}";
            return false;
        }

        if (!TryReadSnapshotStationMapName(set.SetId, snapshotId, out var snapshotMapName, out message))
        {
            message = $"Snapshot '{snapshotId}' cannot be validated: {message}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(metadata.SavedMapName) &&
            !string.Equals(metadata.SavedMapName, snapshotMapName, StringComparison.Ordinal))
        {
            message =
                "Snapshot map mismatch:\n" +
                $"metadata map: {metadata.SavedMapName}\n" +
                $"station map: {snapshotMapName}";
            return false;
        }

        if (IsDevMapMismatch(set.BaseMapPrototype, snapshotMapName))
        {
            message =
                "Snapshot map mismatch:\n" +
                $"set base map: {set.BaseMapPrototype}\n" +
                $"snapshot map: {snapshotMapName}";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private bool TryReadSnapshotStationMapName(
        string setId,
        string snapshotId,
        [NotNullWhen(true)] out string? mapName,
        out string message)
    {
        mapName = null;
        var stationPath = _store.SnapshotStationPath(setId, snapshotId);

        if (!_mapLoader.TryReadFile(stationPath, out var data))
        {
            message = $"could not read {stationPath}.";
            return false;
        }

        return TryReadSerializedMapName(data, out mapName, out message);
    }

    internal static bool TryReadSerializedMapName(
        MappingDataNode data,
        [NotNullWhen(true)] out string? mapName,
        out string message)
    {
        mapName = null;
        var mapYamlIds = new HashSet<int>();

        if (data.TryGet<SequenceDataNode>("maps", out var maps))
        {
            foreach (var node in maps)
            {
                if (node is ValueDataNode value && int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    mapYamlIds.Add(id);
            }
        }

        foreach (var entity in EnumerateSerializedEntities(data))
        {
            if (!TryGetSerializedEntityUid(entity, out var uid))
                continue;

            if (mapYamlIds.Count > 0 && !mapYamlIds.Contains(uid))
                continue;

            if (!TryGetSerializedComponent(entity, "Map", out _))
                continue;

            if (TryGetSerializedMetaName(entity, out mapName))
            {
                message = string.Empty;
                return true;
            }

            message = $"serialized map entity {uid} has no MetaData.name.";
            return false;
        }

        if (mapYamlIds.Count > 0)
        {
            message = $"serialized map entity {string.Join(",", mapYamlIds.OrderBy(id => id))} was not found.";
            return false;
        }

        message = "serialized map root was not found.";
        return false;
    }

    private static IEnumerable<MappingDataNode> EnumerateSerializedEntities(MappingDataNode data)
    {
        if (!data.TryGet<SequenceDataNode>("entities", out var groups))
            yield break;

        foreach (var groupNode in groups)
        {
            if (groupNode is not MappingDataNode group ||
                !group.TryGet<SequenceDataNode>("entities", out var entities))
            {
                continue;
            }

            foreach (var entityNode in entities)
            {
                if (entityNode is MappingDataNode entity)
                    yield return entity;
            }
        }
    }

    private static bool TryGetSerializedEntityUid(MappingDataNode entity, out int uid)
    {
        uid = 0;
        return entity.TryGet<ValueDataNode>("uid", out var uidNode) &&
               int.TryParse(uidNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uid);
    }

    private static bool TryGetSerializedComponent(
        MappingDataNode entity,
        string componentType,
        [NotNullWhen(true)] out MappingDataNode? component)
    {
        component = null;
        if (!entity.TryGet<SequenceDataNode>("components", out var components))
            return false;

        foreach (var componentNode in components)
        {
            if (componentNode is not MappingDataNode candidate ||
                !candidate.TryGet<ValueDataNode>("type", out var typeNode) ||
                !string.Equals(typeNode.Value, componentType, StringComparison.Ordinal))
            {
                continue;
            }

            component = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetSerializedMetaName(MappingDataNode entity, [NotNullWhen(true)] out string? mapName)
    {
        mapName = null;
        if (!TryGetSerializedComponent(entity, "MetaData", out var meta) ||
            !meta.TryGet<ValueDataNode>("name", out var nameNode) ||
            string.IsNullOrWhiteSpace(nameNode.Value))
        {
            return false;
        }

        mapName = nameNode.Value;
        return true;
    }

    private List<EntityUid> CollectGridUids(MapId mapId)
    {
        var result = new List<EntityUid>();
        var query = EntityQueryEnumerator<MapGridComponent, TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out _, out var xform, out var meta))
        {
            if (xform.MapID == mapId && meta.EntityLifeStage < EntityLifeStage.Terminating)
                result.Add(uid);
        }

        return result;
    }

    private int CountEntitiesOnMap(MapId mapId, EntityUid mapEntity, IReadOnlyCollection<EntityUid> gridUids)
    {
        var count = 0;
        foreach (var uid in CollectWeeklyMapEntities(mapEntity, gridUids))
        {
            if (!_metaQuery.TryGetComponent(uid, out var meta) ||
                meta.EntityLifeStage >= EntityLifeStage.Terminating)
                continue;

            count++;
        }

        return count;
    }

    private string GetCurrentOrConfiguredMapName()
    {
        var defaultMap = _gameTicker.DefaultMap;
        if (defaultMap != MapId.Nullspace && _map.MapExists(defaultMap))
        {
            var mapEntity = _map.GetMapOrInvalid(defaultMap);
            if (_metaQuery.TryGetComponent(mapEntity, out var meta) &&
                !string.IsNullOrWhiteSpace(meta.EntityName))
            {
                return meta.EntityName;
            }
        }

        return _gameMapManager.GetSelectedMap()?.MapName ?? "<none>";
    }

    private void ClearActiveWeeklyMapTracking()
    {
        _activeWeeklyMapId = null;
        _activeWeeklyMapEntity = null;
        _activeWeeklyGridIds.Clear();
        _activeWeeklyMapPrototype = null;
        _activeWeeklyMapName = null;
        _state.ActiveWeeklyMapId = null;
        _state.ActiveWeeklyMapEntity = null;
        _state.ActiveWeeklyGridIds = new List<int>();
        _state.ActiveWeeklyMapPrototype = null;
        _state.ActiveWeeklyMapName = null;
    }

    private static bool IsDevMapMismatch(string baseMapPrototype, string? mapName)
    {
        return !string.Equals(baseMapPrototype, "Dev", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(mapName, "Dev", StringComparison.OrdinalIgnoreCase);
    }

    private void SuppressSnapshotMapInitOnlyComponents(
        MapId mapId,
        EntityUid mapEntity,
        IReadOnlyCollection<EntityUid> gridUids)
    {
        var weeklyEntities = CollectWeeklyMapEntities(mapEntity, gridUids);
        var removed = 0;
        removed += RemoveMapInitOnlyComponents<ContainerFillComponent>(weeklyEntities);
        removed += RemoveMapInitOnlyComponents<EntityTableContainerFillComponent>(weeklyEntities);
        removed += RemoveMapInitOnlyComponents<StorageFillComponent>(weeklyEntities);
        removed += RemoveMapInitOnlyComponents<RandomFillSolutionComponent>(weeklyEntities);
        removed += RemoveMapInitOnlyComponents<ConditionalSpawnerComponent>(weeklyEntities);
        removed += RemoveMapInitOnlyComponents<RandomSpawnerComponent>(weeklyEntities);
        removed += RemoveMapInitOnlyComponents<EntityTableSpawnerComponent>(weeklyEntities);
        removed += RemoveMapInitOnlyComponents<RandomDecalSpawnerComponent>(weeklyEntities);

        if (removed > 0)
            _sawmill.Info($"Suppressed {removed} map-init-only fill components before initializing weekly snapshot map {mapId}.");
    }

    private int RemoveMapInitOnlyComponents<T>(HashSet<EntityUid> weeklyEntities) where T : IComponent
    {
        var removed = 0;
        foreach (var uid in weeklyEntities)
        {
            if (!HasComp<T>(uid))
                continue;

            RemComp<T>(uid);
            removed++;
        }

        return removed;
    }

    private void SuppressSnapshotStartingItems(
        MapId mapId,
        EntityUid mapEntity,
        IReadOnlyCollection<EntityUid> gridUids)
    {
        var weeklyEntities = CollectWeeklyMapEntities(mapEntity, gridUids);
        var poweredLightStartupLamps = 0;
        var itemSlotStartingItems = 0;
        var actionGrantStartingActions = 0;
        var binInitialContents = 0;
        var cartridgePreinstalledPrograms = 0;
        var lightReplacerStartingContents = 0;
        var vendingInitialStocks = 0;

        foreach (var uid in weeklyEntities)
        {
            if (TryComp<PoweredLightComponent>(uid, out var poweredLight) &&
                _poweredLight.ClearSpawnedPrototype((uid, poweredLight)))
            {
                poweredLightStartupLamps++;
            }

            if (TryComp<ItemSlotsComponent>(uid, out var itemSlots))
            {
                var itemSlotsChanged = false;
                foreach (var slot in itemSlots.Slots.Values)
                {
                    if (string.IsNullOrEmpty(slot.StartingItem))
                        continue;

                    slot.StartingItem = null;
                    itemSlotsChanged = true;
                    itemSlotStartingItems++;
                }

                if (itemSlotsChanged)
                    Dirty(uid, itemSlots);
            }

            if (TryComp<ActionGrantComponent>(uid, out var actionGrant))
                actionGrantStartingActions += _actionGrant.SuppressMapInitGrants((uid, actionGrant));

            if (TryComp<BinComponent>(uid, out var bin))
                binInitialContents += _bin.SuppressInitialContents((uid, bin));

            if (TryComp<CartridgeLoaderComponent>(uid, out var cartridgeLoader))
            {
                var suppressed = cartridgeLoader.PreinstalledPrograms.Count;
                if (suppressed > 0)
                {
                    cartridgeLoader.PreinstalledPrograms.Clear();
                    Dirty(uid, cartridgeLoader);
                    cartridgePreinstalledPrograms += suppressed;
                }
            }

            if (TryComp<LightReplacerComponent>(uid, out var lightReplacer))
                lightReplacerStartingContents += _lightReplacer.SuppressStartingContents((uid, lightReplacer));

            if (TryComp<VendingMachineComponent>(uid, out var vending) &&
                !vending.SuppressInitialRestock)
            {
                vending.SuppressInitialRestock = true;
                Dirty(uid, vending);
                vendingInitialStocks++;
            }
        }

        if (poweredLightStartupLamps > 0 ||
            itemSlotStartingItems > 0 ||
            actionGrantStartingActions > 0 ||
            binInitialContents > 0 ||
            cartridgePreinstalledPrograms > 0 ||
            lightReplacerStartingContents > 0 ||
            vendingInitialStocks > 0)
        {
            _sawmill.Info(
                "Suppressed snapshot starting items before initializing weekly snapshot map:\n" +
                $"map={mapId}\n" +
                $"poweredLightStartupLamps={poweredLightStartupLamps}\n" +
                $"itemSlotStartingItems={itemSlotStartingItems}\n" +
                $"actionGrantStartingActions={actionGrantStartingActions}\n" +
                $"binInitialContents={binInitialContents}\n" +
                $"cartridgePreinstalledPrograms={cartridgePreinstalledPrograms}\n" +
                $"lightReplacerStartingContents={lightReplacerStartingContents}\n" +
                $"vendingInitialStocks={vendingInitialStocks}");
        }
    }

    private void ApplyContainerPatch(
        MapId mapId,
        EntityUid mapEntity,
        IReadOnlyCollection<EntityUid> gridUids,
        WeeklyContainerPatch patch,
        string setId,
        string snapshotId)
    {
        var yamlToEntity = BuildYamlEntityMap(mapId, mapEntity, gridUids);
        var restored = 0;
        var skipped = 0;

        foreach (var entry in patch.Entries
                     .OrderBy(x => x.OwnerDepth)
                     .ThenBy(x => x.OwnerYamlUid)
                     .ThenBy(x => x.ContainerId, StringComparer.Ordinal)
                     .ThenBy(x => x.Index))
        {
            if (!yamlToEntity.TryGetValue(entry.OwnerYamlUid, out var owner))
            {
                skipped++;
                _sawmill.Warning($"Container patch skipped: owner yaml uid {entry.OwnerYamlUid} missing in snapshot '{snapshotId}'.");
                continue;
            }

            if (!yamlToEntity.TryGetValue(entry.ChildYamlUid, out var child))
            {
                skipped++;
                _sawmill.Warning($"Container patch skipped: child yaml uid {entry.ChildYamlUid} missing in snapshot '{snapshotId}'.");
                continue;
            }

            if (!_containers.TryGetContainer(owner, entry.ContainerId, out var container))
            {
                skipped++;
                _sawmill.Warning($"Container patch skipped: container '{entry.ContainerId}' missing on {ToPrettyString(owner)} in snapshot '{snapshotId}'.");
                continue;
            }

            if (container.Contains(child))
            {
                restored++;
                continue;
            }

            try
            {
                if (_containers.TryGetContainingContainer((child, null, null), out var currentContainer) &&
                    currentContainer != container)
                {
                    _containers.Remove((child, null, null), currentContainer, reparent: false, force: true);
                }

                if (_containers.Insert((child, null, null), container, force: true))
                {
                    restored++;
                    continue;
                }

                skipped++;
                _sawmill.Warning($"Container patch skipped: failed to insert {ToPrettyString(child)} into '{entry.ContainerId}' on {ToPrettyString(owner)}.");
            }
            catch (Exception e)
            {
                skipped++;
                _sawmill.Warning($"Container patch skipped corrupt entry owner={entry.OwnerYamlUid} child={entry.ChildYamlUid} container={entry.ContainerId}: {e.Message}");
            }
        }

        _sawmill.Info($"Applied weekly container patch for set '{setId}' snapshot '{snapshotId}': restored={restored}, skipped={skipped}, entries={patch.Entries.Count}.");
    }

    private Dictionary<int, EntityUid> BuildYamlEntityMap(
        MapId mapId,
        EntityUid mapEntity,
        IReadOnlyCollection<EntityUid> gridUids)
    {
        var result = new Dictionary<int, EntityUid>();
        foreach (var uid in CollectWeeklyMapEntities(mapEntity, gridUids))
        {
            if (!_yamlUidQuery.TryGetComponent(uid, out var yaml))
                continue;

            result.TryAdd(yaml.Uid, uid);
        }

        return result;
    }

    private HashSet<EntityUid> CollectWeeklyMapEntities(EntityUid mapEntity, IReadOnlyCollection<EntityUid> gridUids)
    {
        var entities = new HashSet<EntityUid>();
        var toVisit = new List<EntityUid>();
        if (mapEntity.IsValid())
            toVisit.Add(mapEntity);

        foreach (var grid in gridUids)
        {
            if (grid.IsValid())
                toVisit.Add(grid);
        }

        for (var i = 0; i < toVisit.Count; i++)
        {
            var uid = toVisit[i];
            if (!entities.Add(uid) ||
                !_xformQuery.TryGetComponent(uid, out var xform))
            {
                continue;
            }

            using var children = xform.ChildEnumerator;
            while (children.MoveNext(out var child))
                toVisit.Add(child);
        }

        return entities;
    }

    private void ApplyRoleOverridesToStations(WeeklyModeSet set)
    {
        var query = EntityQueryEnumerator<StationJobsComponent>();
        while (query.MoveNext(out var station, out _))
        {
            ApplyRoleOverridesToStation(station, set);
        }
    }

    private void ApplyRoleOverridesToStation(EntityUid station, WeeklyModeSet set)
    {
        foreach (var jobId in set.DefaultDisabledJobs)
        {
            if (!_stationJobs.TryGetJobSlot(station, jobId, out var current))
                continue;

            var stationSlots = _originalSlots.GetOrNew(station);
            stationSlots.TryAdd(jobId, current);
            _stationJobs.TrySetJobSlot(station, jobId, 0);
        }
    }

    private void RestoreRolesOnStations(IReadOnlyList<string> jobIds)
    {
        if (jobIds.Count == 0)
            return;

        foreach (var (station, slots) in _originalSlots.ToArray())
        {
            foreach (var jobId in jobIds)
            {
                if (!slots.TryGetValue(jobId, out var original))
                    continue;

                if (original == null)
                    _stationJobs.MakeJobUnlimited(station, jobId);
                else
                    _stationJobs.TrySetJobSlot(station, jobId, original.Value, true);

                slots.Remove(jobId);
            }

            if (slots.Count == 0)
                _originalSlots.Remove(station);
        }
    }

    private HashSet<EntityUid> CollectSnapshotExcludedRoots()
    {
        var excluded = _playerManager.Sessions
            .Select(session => session.AttachedEntity)
            .Where(entity => entity is { Valid: true })
            .Select(entity => entity!.Value)
            .ToHashSet();

        var actors = EntityQueryEnumerator<ActorComponent>();
        while (actors.MoveNext(out var uid, out _))
            excluded.Add(uid);

        var ghosts = EntityQueryEnumerator<GhostComponent>();
        while (ghosts.MoveNext(out var uid, out _))
            excluded.Add(uid);

        var minds = EntityQueryEnumerator<MindComponent>();
        while (minds.MoveNext(out var uid, out _))
            excluded.Add(uid);

        var mindContainers = EntityQueryEnumerator<MindContainerComponent>();
        while (mindContainers.MoveNext(out var uid, out var mindContainer))
        {
            if (mindContainer.HasMind || mindContainer.Mind != null)
                excluded.Add(uid);
        }

        return excluded;
    }

    private WeeklyContainerPatch BuildContainerPatch(
        IReadOnlyDictionary<EntityUid, int> yamlUidMap,
        HashSet<EntityUid> excludedRoots)
    {
        var patch = new WeeklyContainerPatch();
        var query = EntityQueryEnumerator<ContainerManagerComponent>();

        while (query.MoveNext(out var owner, out var manager))
        {
            if (IsEntityOrParentExcluded(owner, excludedRoots))
            {
                patch.SkippedExcluded++;
                continue;
            }

            if (!yamlUidMap.TryGetValue(owner, out var ownerYaml))
            {
                patch.SkippedUnserialized++;
                continue;
            }

            var ownerDepth = GetContainerOwnerDepth(owner);
            foreach (var (containerId, container) in manager.Containers.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var contained = container.ContainedEntities;
                for (var i = 0; i < contained.Count; i++)
                {
                    var child = contained[i];
                    if (IsEntityOrParentExcluded(child, excludedRoots))
                    {
                        patch.SkippedExcluded++;
                        continue;
                    }

                    if (!yamlUidMap.TryGetValue(child, out var childYaml))
                    {
                        patch.SkippedUnserialized++;
                        continue;
                    }

                    patch.Entries.Add(new WeeklyContainerEntry
                    {
                        OwnerYamlUid = ownerYaml,
                        OwnerPrototype = GetPrototypeId(owner),
                        ContainerId = containerId,
                        ChildYamlUid = childYaml,
                        ChildPrototype = GetPrototypeId(child),
                        Index = i,
                        OwnerDepth = ownerDepth,
                    });
                }
            }
        }

        patch.Entries.Sort((a, b) =>
        {
            var cmp = a.OwnerDepth.CompareTo(b.OwnerDepth);
            if (cmp != 0)
                return cmp;

            cmp = a.OwnerYamlUid.CompareTo(b.OwnerYamlUid);
            if (cmp != 0)
                return cmp;

            cmp = string.Compare(a.ContainerId, b.ContainerId, StringComparison.Ordinal);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });

        return patch;
    }

    private int GetContainerOwnerDepth(EntityUid owner)
    {
        var depth = 0;
        var current = owner;
        var guard = 0;

        while (current.IsValid() && guard++ < 256)
        {
            if (_containers.TryGetContainingContainer((current, null, null), out _))
                depth++;

            if (!_xformQuery.TryGetComponent(current, out var xform) || !xform.ParentUid.IsValid())
                break;

            current = xform.ParentUid;
        }

        return depth;
    }

    private string GetPrototypeId(EntityUid uid)
    {
        return _metaQuery.TryGetComponent(uid, out var meta)
            ? meta.EntityPrototype?.ID ?? string.Empty
            : string.Empty;
    }

    private bool IsCompleteSnapshotDirectory(ResPath snapshotDirectory, out string message)
    {
        var required = new[]
        {
            WeeklyModeStore.StationFileName,
            WeeklyModeStore.SnapshotMetadataFileName,
            WeeklyModeStore.RoleOverridesFileName,
            WeeklyModeStore.ContainerPatchFileName,
            WeeklyModeStore.IntegrityFileName,
        };

        foreach (var fileName in required)
        {
            var path = _store.SnapshotFilePath(snapshotDirectory, fileName);
            if (_resource.UserData.Exists(path))
                continue;

            message = $"Snapshot bundle is incomplete: missing {fileName}.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private WeeklySnapshotIntegrity BuildIntegrity(string setId, string snapshotId, DateTime createdAtUtc, ResPath snapshotDirectory)
    {
        var integrity = new WeeklySnapshotIntegrity
        {
            SetId = setId,
            SnapshotId = snapshotId,
            CreatedAtUtc = createdAtUtc,
        };

        foreach (var fileName in new[]
                 {
                     WeeklyModeStore.StationFileName,
                     WeeklyModeStore.SnapshotMetadataFileName,
                     WeeklyModeStore.RoleOverridesFileName,
                     WeeklyModeStore.ContainerPatchFileName,
                 })
        {
            var path = _store.SnapshotFilePath(snapshotDirectory, fileName);
            integrity.Files[fileName] = BuildFileIntegrity(path);
        }

        return integrity;
    }

    private WeeklySnapshotFileIntegrity BuildFileIntegrity(ResPath path)
    {
        var bytes = _resource.UserData.ReadAllBytes(path);
        return new WeeklySnapshotFileIntegrity
        {
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        };
    }

    private bool TrySaveMapExcludingPlayers(
        MapId mapId,
        ResPath path,
        HashSet<EntityUid> excludedRoots,
        [NotNullWhen(true)] out Dictionary<EntityUid, int>? yamlUidMap)
    {
        yamlUidMap = null;
        void Filter(Entity<MetaDataComponent> ent, ref bool serializable)
        {
            if (!serializable)
                return;

            if (IsEntityOrParentExcluded(ent.Owner, excludedRoots))
                serializable = false;
        }

        var options = SerializationOptions.Default with
        {
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
        };

        _mapLoader.OnIsSerializable += Filter;
        try
        {
            return _mapLoader.TrySaveMap(mapId, path, out yamlUidMap, options);
        }
        finally
        {
            _mapLoader.OnIsSerializable -= Filter;
        }
    }

    private bool TrySanitizeSerializedSnapshot(
        ResPath stationPath,
        out SnapshotSanitizationResult result,
        out string message)
    {
        result = default;

        if (!_mapLoader.TryReadFile(stationPath, out var data))
        {
            message = $"Failed to read serialized snapshot map '{stationPath}' for sanitation.";
            return false;
        }

        result = SanitizeSerializedSnapshot(data, GetInheritedSnapshotMapInitOnlyComponents);
        if (result.Changed)
            WriteSerializedSnapshot(stationPath, data);

        message = string.Empty;
        return true;
    }

    internal static SnapshotSanitizationResult SanitizeSerializedSnapshot(MappingDataNode data)
    {
        return SanitizeSerializedSnapshot(data, null);
    }

    private static SnapshotSanitizationResult SanitizeSerializedSnapshot(
        MappingDataNode data,
        Func<string, IReadOnlyList<string>>? getInheritedMapInitOnlyComponents)
    {
        var removedStationMembers = 0;
        var removedSuitSensorReferences = 0;
        var removedInvalidContainerReferences = 0;
        var resetMapInitializationFields = 0;
        var suppressedMapInitOnlyComponents = 0;
        var suppressedStartingItems = 0;

        if (!data.TryGet<SequenceDataNode>("entities", out var prototypeGroups))
        {
            return new SnapshotSanitizationResult(
                removedStationMembers,
                removedSuitSensorReferences,
                removedInvalidContainerReferences,
                resetMapInitializationFields,
                suppressedMapInitOnlyComponents,
                suppressedStartingItems);
        }

        foreach (var groupNode in prototypeGroups)
        {
            if (groupNode is not MappingDataNode group ||
                !group.TryGet<SequenceDataNode>("entities", out var entities))
            {
                continue;
            }

            var protoId = group.TryGet<ValueDataNode>("proto", out var protoNode)
                ? protoNode.Value ?? string.Empty
                : string.Empty;
            var inheritedMapInitOnlyComponents = getInheritedMapInitOnlyComponents?.Invoke(protoId)
                                                 ?? Array.Empty<string>();

            foreach (var entityNode in entities)
            {
                if (entityNode is not MappingDataNode entity)
                    continue;

                entity.TryGet<SequenceDataNode>("components", out var components);

                if (entity.Remove("mapInit"))
                    resetMapInitializationFields++;
                if (entity.Remove("paused"))
                    resetMapInitializationFields++;

                List<string>? missingMapInitOnlyComponents = null;
                if (inheritedMapInitOnlyComponents.Count > 0)
                    missingMapInitOnlyComponents = new List<string>(inheritedMapInitOnlyComponents);

                for (var i = (components?.Count ?? 0) - 1; i >= 0; i--)
                {
                    if (components![i] is not MappingDataNode component ||
                        !TryGetSerializedComponentType(component, out var type))
                    {
                        continue;
                    }

                    if (IsSnapshotMapInitOnlyComponent(type))
                    {
                        components.RemoveAt(i);
                        missingMapInitOnlyComponents ??= new List<string>();
                        missingMapInitOnlyComponents.Add(type);
                        continue;
                    }

                    switch (type)
                    {
                        case "StationMember":
                            components.RemoveAt(i);
                            removedStationMembers++;
                            break;
                        case "SuitSensor":
                            if (component.Remove("station"))
                                removedSuitSensorReferences++;
                            if (component.Remove("user"))
                                removedSuitSensorReferences++;
                            break;
                        case "ContainerContainer":
                            removedInvalidContainerReferences += RemoveInvalidContainerReferences(component);
                            break;
                        case "PoweredLight":
                            if (SuppressPoweredLightStartupLamp(component))
                                suppressedStartingItems++;
                            break;
                        case "ItemSlots":
                            suppressedStartingItems += SuppressSerializedStartingItems(component);
                            break;
                        case "ActionGrant":
                            suppressedStartingItems += SuppressActionGrantStartingActions(component);
                            break;
                        case "Bin":
                            suppressedStartingItems += SuppressSequenceField(component, "initialContents");
                            break;
                        case "CartridgeLoader":
                            suppressedStartingItems += SuppressSequenceField(component, "preinstalled");
                            break;
                        case "LightReplacer":
                            suppressedStartingItems += SuppressEntitySpawnEntries(component, "contents");
                            break;
                        case "VendingMachine":
                            if (SuppressVendingInitialRestock(component))
                                suppressedStartingItems++;
                            break;
                        case "Map":
                            if (component.Remove("mapInitialized"))
                                resetMapInitializationFields++;
                            if (component.Remove("mapPaused"))
                                resetMapInitializationFields++;
                            break;
                    }
                }

                if (missingMapInitOnlyComponents is { Count: > 0 })
                {
                    suppressedMapInitOnlyComponents += AddMissingComponents(
                        entity,
                        missingMapInitOnlyComponents.Distinct(StringComparer.Ordinal));
                }
            }
        }

        return new SnapshotSanitizationResult(
            removedStationMembers,
            removedSuitSensorReferences,
            removedInvalidContainerReferences,
            resetMapInitializationFields,
            suppressedMapInitOnlyComponents,
            suppressedStartingItems);
    }

    private static bool SuppressPoweredLightStartupLamp(MappingDataNode component)
    {
        if (component.TryGet<ValueDataNode>("hasLampOnSpawn", out var hasLampOnSpawn) &&
            hasLampOnSpawn.IsNull)
        {
            return false;
        }

        component["hasLampOnSpawn"] = ValueDataNode.Null();
        return true;
    }

    private static int SuppressSerializedStartingItems(DataNode node)
    {
        var suppressed = 0;
        switch (node)
        {
            case MappingDataNode mapping:
                foreach (var (key, value) in mapping.ToArray())
                {
                    if (key == "startingItem" &&
                        value is not ValueDataNode { IsNull: true })
                    {
                        mapping[key] = ValueDataNode.Null();
                        suppressed++;
                        continue;
                    }

                    suppressed += SuppressSerializedStartingItems(value);
                }

                break;
            case SequenceDataNode sequence:
                foreach (var value in sequence)
                    suppressed += SuppressSerializedStartingItems(value);
                break;
        }

        return suppressed;
    }

    private static int SuppressActionGrantStartingActions(MappingDataNode component)
    {
        return SuppressSequenceField(component, "actions", createWhenMissing: true);
    }

    private static bool SuppressVendingInitialRestock(MappingDataNode component)
    {
        if (component.TryGet<ValueDataNode>("suppressInitialRestock", out var suppressed) &&
            bool.TryParse(suppressed.Value, out var value) &&
            value)
        {
            return false;
        }

        component["suppressInitialRestock"] = new ValueDataNode("true");
        return true;
    }

    private static int SuppressSequenceField(
        MappingDataNode component,
        string field,
        bool createWhenMissing = false)
    {
        if (component.TryGet<SequenceDataNode>(field, out var sequence))
        {
            var suppressed = sequence.Count;
            sequence.Clear();
            return suppressed;
        }

        if (!createWhenMissing)
            return 0;

        component[field] = new SequenceDataNode();
        return 1;
    }

    private static int SuppressEntitySpawnEntries(MappingDataNode component, string field)
    {
        if (!component.TryGet<SequenceDataNode>(field, out var sequence))
            return 0;

        var suppressed = 0;
        foreach (var entry in sequence.OfType<MappingDataNode>())
        {
            if (entry.TryGet<ValueDataNode>("amount", out var amount) &&
                int.TryParse(amount.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                suppressed += Math.Max(parsed, 0);
            }
            else
            {
                suppressed++;
            }
        }

        sequence.Clear();
        return suppressed;
    }

    private IReadOnlyList<string> GetInheritedSnapshotMapInitOnlyComponents(string protoId)
    {
        if (string.IsNullOrWhiteSpace(protoId) ||
            !_prototype.TryIndex<EntityPrototype>(protoId, out var proto))
        {
            return Array.Empty<string>();
        }

        List<string>? result = null;
        foreach (var componentName in SnapshotMapInitOnlyComponents)
        {
            if (!proto.Components.ContainsKey(componentName))
                continue;

            result ??= new List<string>();
            result.Add(componentName);
        }

        return result != null ? result : Array.Empty<string>();
    }

    private static bool IsSnapshotMapInitOnlyComponent(string type)
    {
        return SnapshotMapInitOnlyComponents.Contains(type, StringComparer.Ordinal);
    }

    private static int AddMissingComponents(MappingDataNode entity, IEnumerable<string> componentNames)
    {
        if (!entity.TryGet<SequenceDataNode>("missingComponents", out var missingComponents))
        {
            missingComponents = new SequenceDataNode();
            entity.Add("missingComponents", missingComponents);
        }

        var existing = missingComponents
            .OfType<ValueDataNode>()
            .Select(node => node.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var componentName in componentNames)
        {
            if (!existing.Add(componentName))
                continue;

            missingComponents.Add(new ValueDataNode(componentName));
            added++;
        }

        if (missingComponents.Count == 0)
            entity.Remove("missingComponents");

        return added;
    }

    private static bool TryGetSerializedComponentType(MappingDataNode component, [NotNullWhen(true)] out string? type)
    {
        type = null;
        if (!component.TryGet<ValueDataNode>("type", out var typeNode) ||
            string.IsNullOrWhiteSpace(typeNode.Value))
        {
            return false;
        }

        type = typeNode.Value;
        return true;
    }

    private static int RemoveInvalidContainerReferences(DataNode node)
    {
        var removed = 0;
        switch (node)
        {
            case MappingDataNode mapping:
                foreach (var (key, value) in mapping.ToArray())
                {
                    if (key == "ent" && IsInvalidEntityReference(value))
                    {
                        mapping[key] = ValueDataNode.Null();
                        removed++;
                        continue;
                    }

                    removed += RemoveInvalidContainerReferences(value);
                }

                break;
            case SequenceDataNode sequence:
                for (var i = sequence.Count - 1; i >= 0; i--)
                {
                    if (IsInvalidEntityReference(sequence[i]))
                    {
                        sequence.RemoveAt(i);
                        removed++;
                        continue;
                    }

                    removed += RemoveInvalidContainerReferences(sequence[i]);
                }

                break;
        }

        return removed;
    }

    private static bool IsInvalidEntityReference(DataNode node)
    {
        return node is ValueDataNode { Value: "invalid" };
    }

    private void WriteSerializedSnapshot(ResPath path, MappingDataNode data)
    {
        path = path.ToRootedPath();
        _resource.UserData.CreateDir(path.Directory);
        using var writer = _resource.UserData.OpenWriteText(path);
        var document = new YamlDocument(data.ToYaml());
        var stream = new YamlStream { document };
        stream.Save(new YamlMappingFix(new Emitter(writer)), false);
    }

    private bool IsEntityOrParentExcluded(EntityUid uid, HashSet<EntityUid> excludedRoots)
    {
        if (excludedRoots.Count == 0)
            return false;

        var current = uid;
        var depth = 0;
        while (current.IsValid() && depth++ < 256)
        {
            if (excludedRoots.Contains(current))
                return true;

            if (!_xformQuery.TryGetComponent(current, out var xform))
                return false;

            current = xform.ParentUid;
        }

        return false;
    }

    private bool TryLoadMutableSet(string setId, [NotNullWhen(true)] out WeeklyModeSet? set, out string message)
    {
        set = null;

        if (!WeeklyModeStore.IsSafeId(setId))
        {
            message = "Invalid setId. Use only ASCII letters, digits, '-', '_' or '.'.";
            return false;
        }

        if (!_store.TryLoadSet(setId, out set))
        {
            message = $"Weekly set '{setId}' was not found.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryNormalizeAlias(string rawAlias, out string alias, out string message)
    {
        alias = rawAlias.Trim().Trim('"');

        if (alias.Length == 0)
        {
            message = "Alias may not be empty.";
            return false;
        }

        if (alias.Length > 64)
        {
            message = "Alias may not be longer than 64 characters.";
            return false;
        }

        foreach (var c in alias)
        {
            if (char.IsControl(c))
            {
                message = "Alias may not contain control characters.";
                return false;
            }

            if (c is '[' or ']')
            {
                message = "Alias may not contain markup brackets '[' or ']'.";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{age.TotalDays:F1}d";

        if (age.TotalHours >= 1)
            return $"{age.TotalHours:F1}h";

        if (age.TotalMinutes >= 1)
            return $"{age.TotalMinutes:F1}m";

        return $"{Math.Max(0, age.TotalSeconds):F0}s";
    }

    private void PruneAutosaves(WeeklyModeSet set)
    {
        var retain = Math.Max(1, set.RetainAutosaves);
        var autos = new List<WeeklySnapshotMetadata>();

        foreach (var snapshotId in set.Snapshots)
        {
            if (_store.TryLoadSnapshot(set.SetId, snapshotId, out var metadata) &&
                metadata.Kind == WeeklySnapshotKind.Auto)
            {
                autos.Add(metadata);
            }
        }

        foreach (var metadata in autos.OrderByDescending(x => x.CreatedAtUtc).Skip(retain))
        {
            if (metadata.SnapshotId == set.CurrentSnapshot)
                continue;

            _store.DeleteSnapshot(set.SetId, metadata.SnapshotId);
            set.Snapshots.Remove(metadata.SnapshotId);
        }
    }

    private bool IsActiveSet(string setId)
    {
        return _state.IsActive && _state.ActiveSetId == setId;
    }

    private bool EnsureEnabled(out string message)
    {
        if (_enabled)
        {
            message = string.Empty;
            return true;
        }

        message = "Weekly mode is disabled. Set weekly_mode.enabled to true first.";
        return false;
    }

    private bool TryEnterOperation(out string message)
    {
        if (_operationInProgress)
        {
            message = "A weekly mode operation is already in progress.";
            return false;
        }

        _operationInProgress = true;
        message = string.Empty;
        return true;
    }

    private void RestartForWeeklyMap()
    {
        _nextAutosaveAt = null;
        _originalSlots.Clear();
        _gameTicker.RestartRound();
    }

    private void RebuildStore(string root)
    {
        _store = new WeeklyModeStore(_resource, root);
    }

    private static void CopyState(WeeklyModeRuntimeState source, WeeklyModeRuntimeState target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.IsActive = source.IsActive;
        target.ActiveSetId = source.ActiveSetId;
        target.ActiveSnapshotId = source.ActiveSnapshotId;
        target.PendingSnapshotId = source.PendingSnapshotId;
        target.ActiveWeeklyMapId = source.ActiveWeeklyMapId;
        target.ActiveWeeklyMapEntity = source.ActiveWeeklyMapEntity;
        target.ActiveWeeklyGridIds = source.ActiveWeeklyGridIds?.ToList() ?? new List<int>();
        target.ActiveWeeklyMapPrototype = source.ActiveWeeklyMapPrototype;
        target.ActiveWeeklyMapName = source.ActiveWeeklyMapName;
        target.StartedAtUtc = source.StartedAtUtc;
        target.StartedBy = source.StartedBy;
    }

    private static string BuildSnapshotId(WeeklySnapshotKind kind)
    {
        var prefix = kind switch
        {
            WeeklySnapshotKind.Auto => "auto",
            WeeklySnapshotKind.Manual => "manual",
            WeeklySnapshotKind.Endshift => "endshift",
            WeeklySnapshotKind.RollbackPoint => "rollback-point",
            WeeklySnapshotKind.RollbackBackup => "rollback-backup",
            _ => "snapshot",
        };

        return $"{prefix}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
    }
}
