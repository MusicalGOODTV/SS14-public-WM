using System.Linq;
using System.Globalization;
using Content.Server.WeeklyMode.Systems;
using Content.Shared.Administration;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Maps;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Content.Shared.Roles;
using Content.Shared.WeeklyMode;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration.Commands.WeeklyMode;

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmSetCreateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IResourceManager _resource = default!;
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.set.create";
    public override string Description => "Creates a weekly mode set.";
    public override string Help => "wm.set.create <setId> <mapPath> [displayName]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        var displayName = args.Length >= 3 ? string.Join(' ', args.Skip(2)) : null;
        if (_weekly.TryCreateSet(args[0], args[1], displayName, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("Set id"),
            2 => CompletionResult.FromHintOptions(CompletionHelper.ContentFilePath(args[1], _resource), "Map path"),
            3 => CompletionResult.FromHint("Display name"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmSetMapCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IResourceManager _resource = default!;
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.set.map";
    public override string Description => "Changes a weekly mode set map path before start.";
    public override string Help => "wm.set.map <setId> <mapPath>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TrySetMap(args[0], args[1], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("Set id"),
            2 => CompletionResult.FromHintOptions(CompletionHelper.ContentFilePath(args[1], _resource), "Map path"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmSetListCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.set.list";
    public override string Description => "Lists weekly mode sets.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError("wm.set.list");
            return;
        }

        shell.WriteLine(_weekly.ListSets());
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmStartCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.start";
    public override string Description => "Starts or resumes a weekly mode round.";
    public override string Help => "wm.start <setId> [snapshotId]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            shell.WriteError(Help);
            return;
        }

        var startedBy = shell.Player?.Name ?? "server console";
        if (_weekly.TryStart(args[0], args.Length == 2 ? args[1] : null, startedBy, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmSaveCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.save";
    public override string Description => "Saves the current weekly station map as a checkpoint.";
    public override string Help => "wm.save <setId> [note]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError(Help);
            return;
        }

        var note = args.Length > 1
            ? string.Join(' ', args.Skip(1))
            : string.Empty;
        var createdBy = shell.Player?.Name ?? "server console";

        if (_weekly.TrySaveSnapshot(args[0], WeeklySnapshotKind.Manual, note, createdBy, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRollbackCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.rollback";
    public override string Description => "Restarts weekly mode into a selected snapshot.";
    public override string Help => "wm.rollback <setId> <snapshotId> [--force|token]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            shell.WriteError(Help);
            return;
        }

        var startedBy = shell.Player?.Name ?? "server console";
        bool success;
        string message;

        if (args.Length == 2)
            success = _weekly.TryPrepareRollback(args[0], args[1], startedBy, out _, out message);
        else if (args[2] == "--force")
            success = _weekly.TryRollback(args[0], args[1], startedBy, out message);
        else
            success = _weekly.TryConfirmRollback(args[0], args[1], args[2], startedBy, out message);

        if (success)
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmStatusCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.status";
    public override string Description => "Shows weekly mode status.";
    public override string Help => "wm.status [setId]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.GetStatus(args.Length == 1 ? args[0] : null));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmSnapshotsCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.snapshots";
    public override string Description => "Lists snapshots for a weekly set.";
    public override string Help => "wm.snapshots <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListSnapshots(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmSnapshotDeleteCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.snapshot.delete";
    public override string Description => "Deletes a non-current weekly snapshot.";
    public override string Help => "wm.snapshot.delete <setId> <snapshotId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryDeleteSnapshot(args[0], args[1], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesDisableCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.disable";
    public override string Description => "Disables weekly mode roles for a set.";
    public override string Help => "wm.roles.disable <setId> <jobId...>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryDisableRoles(args[0], args.Skip(1).ToArray(), out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length >= 2
            ? CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype")
            : CompletionResult.FromHint("Set id");
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesEnableCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.enable";
    public override string Description => "Re-enables weekly mode roles for a set.";
    public override string Help => "wm.roles.enable <setId> <jobId...>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryEnableRoles(args[0], args.Skip(1).ToArray(), out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length >= 2
            ? CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype")
            : CompletionResult.FromHint("Set id");
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesRenameCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.rename";
    public override string Description => "Adds a runtime role alias for a weekly set.";
    public override string Help => "wm.roles.rename <setId> <jobId> <alias>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3)
        {
            shell.WriteError(Help);
            return;
        }

        var alias = string.Join(' ', args.Skip(2));
        if (_weekly.TryRenameRole(args[0], args[1], alias, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("Set id"),
            2 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype"),
            3 => CompletionResult.FromHint("Alias"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesRenameBatchCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.rename-batch";
    public override string Description => "Adds multiple runtime role aliases for a weekly set.";
    public override string Help => "wm.roles.rename-batch <setId> JobId=\"Alias\";JobId=\"Alias\"";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        var batch = string.Join(' ', args.Skip(1));
        if (_weekly.TryRenameRolesBatch(args[0], batch, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesAliasesCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.aliases";
    public override string Description => "Lists runtime role aliases for a weekly set.";
    public override string Help => "wm.roles.aliases <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListRoleAliases(args[0]));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesRenameClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.rename-clear";
    public override string Description => "Clears runtime role aliases for a weekly set.";
    public override string Help => "wm.roles.rename-clear <setId> [jobId...]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearRoleAliases(args[0], args.Skip(1).ToArray(), out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length >= 2
            ? CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype")
            : CompletionResult.FromHint("Set id");
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.clear";
    public override string Description => "Clears weekly disabled roles and aliases for a set.";
    public override string Help => "wm.roles.clear <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearRoles(args[0], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesLimitCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.limit";
    public override string Description => "Sets a weekly role limit for a set.";
    public override string Help => "wm.roles.limit <setId> <jobId> <count>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3 ||
            !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TrySetRoleLimit(args[0], args[1], count, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("Set id"),
            2 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype"),
            3 => CompletionResult.FromHint("Count"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesLimitsCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.limits";
    public override string Description => "Lists weekly role limits for a set.";
    public override string Help => "wm.roles.limits <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListRoleLimits(args[0]));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesLimitClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.limit-clear";
    public override string Description => "Clears one weekly role limit for a set.";
    public override string Help => "wm.roles.limit-clear <setId> <jobId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearRoleLimit(args[0], args[1], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("Set id"),
            2 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesLimitClearAllCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.limit-clear-all";
    public override string Description => "Clears all weekly role limits for a set.";
    public override string Help => "wm.roles.limit-clear-all <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearRoleLimits(args[0], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRolesForceCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.force";
    public override string Description => "Forces a campaign job assignment for a specific account.";
    public override string Help => "wm.roles.force <setId> <ckey-or-uuid> <jobId> [--bypass-playtime]";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 3 or > 4 ||
            args.Length == 4 && args[3] != "--bypass-playtime")
        {
            shell.WriteError(Help);
            return;
        }

        var target = await _weekly.ResolveForcedRoleTargetAsync(args[1]);
        if (!target.Success)
        {
            shell.WriteError(target.Message);
            return;
        }

        var createdBy = shell.Player?.Name ?? "server console";
        if (_weekly.TryForceRole(args[0], target.UserId, target.LastKnownCKey, args[2], args.Length == 4, createdBy, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("Set id"),
            2 => CompletionResult.FromHint("CKey or UUID"),
            3 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype"),
            4 => CompletionResult.FromHintOptions(new[] { "--bypass-playtime" }, "Optional playtime bypass"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRolesForceUpdateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.force-update";
    public override string Description => "Updates a campaign forced job assignment.";
    public override string Help => "wm.roles.force-update <setId> <ckey-or-uuid> <newJobId> [--bypass-playtime|--no-bypass-playtime]";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            shell.WriteError(Help);
            return;
        }

        bool? bypass = null;
        if (args.Length == 4)
        {
            bypass = args[3] switch
            {
                "--bypass-playtime" => true,
                "--no-bypass-playtime" => false,
                _ => null,
            };

            if (bypass == null)
            {
                shell.WriteError(Help);
                return;
            }
        }

        var target = await _weekly.ResolveForcedRoleTargetAsync(args[1]);
        if (!target.Success)
        {
            shell.WriteError(target.Message);
            return;
        }

        var updatedBy = shell.Player?.Name ?? "server console";
        if (_weekly.TryUpdateForcedRole(args[0], target.UserId, target.LastKnownCKey, args[2], updatedBy, bypass, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("Set id"),
            2 => CompletionResult.FromHint("CKey or UUID"),
            3 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<JobPrototype>(), "Job prototype"),
            4 => CompletionResult.FromHintOptions(new[] { "--bypass-playtime", "--no-bypass-playtime" }, "Optional playtime flag"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesForceListCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.force-list";
    public override string Description => "Lists campaign forced job assignments.";
    public override string Help => "wm.roles.force-list <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListForcedRoles(args[0]));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRolesForceShowCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.force-show";
    public override string Description => "Shows one campaign forced job assignment.";
    public override string Help => "wm.roles.force-show <setId> <ckey-or-uuid>";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        var target = await _weekly.ResolveForcedRoleTargetAsync(args[1]);
        if (!target.Success)
        {
            shell.WriteError(target.Message);
            return;
        }

        shell.WriteLine(_weekly.ShowForcedRole(args[0], target.UserId, args[1]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRolesForceClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.force-clear";
    public override string Description => "Clears one campaign forced job assignment.";
    public override string Help => "wm.roles.force-clear <setId> <ckey-or-uuid>";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        var target = await _weekly.ResolveForcedRoleTargetAsync(args[1]);
        if (!target.Success)
        {
            shell.WriteError(target.Message);
            return;
        }

        var removedBy = shell.Player?.Name ?? "server console";
        if (_weekly.TryClearForcedRole(args[0], target.UserId, args[1], removedBy, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRolesForceClearAllCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.roles.force-clear-all";
    public override string Description => "Clears all campaign forced job assignments.";
    public override string Help => "wm.roles.force-clear-all <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var removedBy = shell.Player?.Name ?? "server console";
        if (_weekly.TryClearAllForcedRoles(args[0], removedBy, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmAutosaveSetCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.autosave.set";
    public override string Description => "Sets weekly autosave interval and warning minutes for a set.";
    public override string Help => "wm.autosave.set <setId> <intervalMinutes> <warningMinutes>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3 ||
            !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalMinutes) ||
            !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var warningMinutes))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TrySetAutosave(args[0], intervalMinutes, warningMinutes, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmPersistenceMobsCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.persistence.mobs";
    public override string Description => "Toggles autonomous mob persistence for a weekly set.";
    public override string Help => "wm.persistence.mobs <setId> <true|false>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !WeeklyModeCommandParsing.TryParseBool(args[1], out var enabled))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TrySetPersistAutonomousMobs(args[0], enabled, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmPersistenceExcludePrototypeCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.persistence.exclude-prototype";
    public override string Description => "Excludes an entity prototype and descendants from autonomous mob persistence.";
    public override string Help => "wm.persistence.exclude-prototype <setId> <prototypeId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryExcludeMobPrototype(args[0], args[1], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 2
            ? CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<EntityPrototype>(), "Entity prototype")
            : CompletionResult.FromHint("Set id");
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmPersistenceExcludeListCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.persistence.exclude-list";
    public override string Description => "Lists excluded autonomous mob prototypes for a weekly set.";
    public override string Help => "wm.persistence.exclude-list <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListExcludedMobPrototypes(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmPersistenceExcludeClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.persistence.exclude-clear";
    public override string Description => "Removes one autonomous mob prototype exclusion from a weekly set.";
    public override string Help => "wm.persistence.exclude-clear <setId> <prototypeId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearExcludedMobPrototype(args[0], args[1], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmAccessMinPlaytimeCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.access.min-playtime";
    public override string Description => "Sets minimum overall server playtime for weekly participation.";
    public override string Help => "wm.access.min-playtime <setId> <hours>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 ||
            !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TrySetMinPlaytime(args[0], hours, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmAccessDiscordCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.access.discord";
    public override string Description => "Sets the Discord channel/link shown in weekly access notices.";
    public override string Help => "wm.access.discord <setId> <channel-or-link>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        var channel = string.Join(' ', args.Skip(1));
        if (_weekly.TrySetDiscordChannel(args[0], channel, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmAccessStatusCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.access.status";
    public override string Description => "Shows weekly access restrictions.";
    public override string Help => "wm.access.status <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.GetAccessStatus(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmAccessClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.access.clear";
    public override string Description => "Clears weekly access restrictions.";
    public override string Help => "wm.access.clear <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearAccess(args[0], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmTechCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.tech";
    public override string Description => "Adds or removes a weekly research technology entry.";
    public override string Help => "wm.tech <setId> <branch> <add|remove> <technologyId> [cost] [tier] [recipeId...]\nwm.tech <setId> <branch> remove <technologyId> [--force]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 4)
        {
            shell.WriteError(Help);
            return;
        }

        var setId = args[0];
        var branch = args[1];
        var action = args[2];
        var technologyId = args[3];

        if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length is not (4 or 5) ||
                args.Length == 5 && !string.Equals(args[4], "--force", StringComparison.OrdinalIgnoreCase))
            {
                shell.WriteError(Help);
                return;
            }

            var force = args.Length == 5;
            if (_weekly.TryRemoveTechnology(setId, branch, technologyId, force, out var removeMessage))
                shell.WriteLine(removeMessage);
            else
                shell.WriteError(removeMessage);
            return;
        }

        if (!string.Equals(action, "add", StringComparison.OrdinalIgnoreCase) ||
            args.Length < 6 ||
            !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cost) ||
            !int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryAddTechnology(setId, branch, technologyId, cost, tier, args.Skip(6).ToArray(), out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            2 => CompletionResult.FromHintOptions(new[] { "industrial", "arsenal", "experimental", "service" }, "Branch"),
            3 => CompletionResult.FromHintOptions(new[] { "add", "remove" }, "Action"),
            7 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<LatheRecipePrototype>(), "Recipe prototype"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmTechUpdateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.tech.update";
    public override string Description => "Updates an existing weekly research technology entry.";
    public override string Help => "wm.tech.update <setId> <technologyId> <branch> <cost> <tier> [recipeId...]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 5 ||
            !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cost) ||
            !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryUpdateTechnology(args[0], args[1], args[2], cost, tier, args.Skip(5).ToArray(), out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            3 => CompletionResult.FromHintOptions(new[] { "industrial", "arsenal", "experimental", "service" }, "Branch"),
            6 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<LatheRecipePrototype>(), "Recipe prototype"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmTechListCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.tech.list";
    public override string Description => "Lists weekly research technologies.";
    public override string Help => "wm.tech.list <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListTechnologies(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmTechClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.tech.clear";
    public override string Description => "Clears all weekly research technologies.";
    public override string Help => "wm.tech.clear <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearTechnologies(args[0], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmTechClearBranchCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.tech.clear-branch";
    public override string Description => "Clears one weekly research branch.";
    public override string Help => "wm.tech.clear-branch <setId> <branch>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearTechnologyBranch(args[0], args[1], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmTechValidateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.tech.validate";
    public override string Description => "Validates weekly research technologies.";
    public override string Help => "wm.tech.validate <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ValidateTechnologies(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe";
    public override string Description => "Adds a campaign-only weekly lathe recipe.";
    public override string Help => "wm.recipe <setId> add <recipeId> <resultPrototype> <resultAmount> <productionTimeSeconds> <latheTargets> <materialId:amount> [materialId:amount ...]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 8 ||
            !string.Equals(args[1], "add", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultAmount) ||
            !double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var productionTimeSeconds))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryAddRecipe(args[0], args[2], args[3], resultAmount, productionTimeSeconds, args[6], args.Skip(7).ToArray(), out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            2 => CompletionResult.FromHintOptions(new[] { "add" }, "Action"),
            4 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<EntityPrototype>(), "Result entity prototype"),
            7 => CompletionResult.FromHintOptions(new[] { "protolathe", "security", "medical", "engineering", "service", "science", "cargo", "civilian", "all" }, "Lathe targets"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeUpdateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.update";
    public override string Description => "Updates a campaign-only weekly lathe recipe.";
    public override string Help => "wm.recipe.update <setId> <recipeId> <result|time|targets|materials> <args...>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 4)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryUpdateRecipe(args[0], args[1], args[2], args.Skip(3).ToArray(), out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            3 => CompletionResult.FromHintOptions(new[] { "result", "time", "targets", "materials" }, "Field"),
            4 when args.Length >= 3 && string.Equals(args[2], "result", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<EntityPrototype>(), "Result entity prototype"),
            4 when args.Length >= 3 && string.Equals(args[2], "targets", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHintOptions(new[] { "protolathe", "security", "medical", "engineering", "service", "science", "cargo", "civilian", "all" }, "Lathe targets"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeTargetAddCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.target-add";
    public override string Description => "Adds a target to a campaign-only weekly recipe.";
    public override string Help => "wm.recipe.target-add <setId> <recipeId> <latheTarget>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryAddRecipeTarget(args[0], args[1], args[2], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeTargetRemoveCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.target-remove";
    public override string Description => "Removes a target from a campaign-only weekly recipe.";
    public override string Help => "wm.recipe.target-remove <setId> <recipeId> <latheTarget>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryRemoveRecipeTarget(args[0], args[1], args[2], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeLinkCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.link";
    public override string Description => "Links a campaign-only weekly recipe to a weekly technology.";
    public override string Help => "wm.recipe.link <setId> <recipeId> <technologyId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryLinkRecipe(args[0], args[1], args[2], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeUnlinkCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.unlink";
    public override string Description => "Unlinks a campaign-only weekly recipe from a weekly technology.";
    public override string Help => "wm.recipe.unlink <setId> <recipeId> <technologyId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryUnlinkRecipe(args[0], args[1], args[2], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRecipeListCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.list";
    public override string Description => "Lists campaign-only weekly recipes.";
    public override string Help => "wm.recipe.list <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListRecipes(args[0]));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRecipeShowCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.show";
    public override string Description => "Shows a campaign-only weekly recipe.";
    public override string Help => "wm.recipe.show <setId> <recipeId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ShowRecipe(args[0], args[1]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeRemoveCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.remove";
    public override string Description => "Removes a campaign-only weekly recipe.";
    public override string Help => "wm.recipe.remove <setId> <recipeId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryRemoveRecipe(args[0], args[1], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmRecipeValidateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.validate";
    public override string Description => "Validates campaign-only weekly recipes.";
    public override string Help => "wm.recipe.validate <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ValidateRecipes(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmRecipeClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.recipe.clear";
    public override string Description => "Clears all campaign-only weekly recipes.";
    public override string Help => "wm.recipe.clear <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearRecipes(args[0], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmCargoCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.cargo";
    public override string Description => "Adds or removes a weekly cargo product entry.";
    public override string Help => "wm.cargo <setId> <add|remove> <productId> [category] [cost] [boxed] [amount] [itemPrototype]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3)
        {
            shell.WriteError(Help);
            return;
        }

        var setId = args[0];
        var action = args[1];
        var productId = args[2];

        if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 3)
            {
                shell.WriteError(Help);
                return;
            }

            if (_weekly.TryRemoveCargoProduct(setId, productId, out var removeMessage))
                shell.WriteLine(removeMessage);
            else
                shell.WriteError(removeMessage);
            return;
        }

        if (!string.Equals(action, "add", StringComparison.OrdinalIgnoreCase) ||
            args.Length != 8 ||
            !int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cost) ||
            !WeeklyModeCommandParsing.TryParseBool(args[5], out var boxed) ||
            !int.TryParse(args[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryAddCargoProduct(setId, productId, args[3], cost, boxed, amount, args[7], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            2 => CompletionResult.FromHintOptions(new[] { "add", "remove" }, "Action"),
            6 => CompletionResult.FromHintOptions(new[] { "true", "false" }, "Boxed"),
            8 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<EntityPrototype>(), "Item prototype"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmCargoUpdateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.cargo.update";
    public override string Description => "Updates an existing weekly cargo product entry.";
    public override string Help => "wm.cargo.update <setId> <productId> <category> <cost> <boxed> <amount> <itemPrototype>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 7 ||
            !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cost) ||
            !WeeklyModeCommandParsing.TryParseBool(args[4], out var boxed) ||
            !int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryUpdateCargoProduct(args[0], args[1], args[2], cost, boxed, amount, args[6], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            5 => CompletionResult.FromHintOptions(new[] { "true", "false" }, "Boxed"),
            7 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<EntityPrototype>(), "Item prototype"),
            _ => CompletionResult.Empty
        };
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmCargoListCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.cargo.list";
    public override string Description => "Lists weekly cargo products.";
    public override string Help => "wm.cargo.list <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ListCargoProducts(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmCargoClearCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.cargo.clear";
    public override string Description => "Clears all weekly cargo products.";
    public override string Help => "wm.cargo.clear <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryClearCargoProducts(args[0], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmCargoClearCategoryCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.cargo.clear-category";
    public override string Description => "Clears weekly cargo products in one category.";
    public override string Help => "wm.cargo.clear-category <setId> <category>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        var category = string.Join(' ', args.Skip(1));
        if (_weekly.TryClearCargoCategory(args[0], category, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmCargoValidateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.cargo.validate";
    public override string Description => "Validates weekly cargo products.";
    public override string Help => "wm.cargo.validate <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ValidateCargoProducts(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmGamerulesRandomCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.gamerules.random";
    public override string Description => "Toggles automatic random gamerules for a weekly set.";
    public override string Help => "wm.gamerules.random <setId> <true|false>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !WeeklyModeCommandParsing.TryParseBool(args[1], out var enabled))
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TrySetRandomGameRules(args[0], enabled, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmGamerulesStatusCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.gamerules.status";
    public override string Description => "Shows weekly gamerule settings.";
    public override string Help => "wm.gamerules.status <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.GetGameRulesStatus(args[0]));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmConfigShowCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.config.show";
    public override string Description => "Shows weekly campaign configuration.";
    public override string Help => "wm.config.show <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ShowConfig(args[0]));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmConfigValidateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.config.validate";
    public override string Description => "Validates weekly campaign configuration.";
    public override string Help => "wm.config.validate <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ValidateConfig(args[0]));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class WmConfigExportCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.config.export";
    public override string Description => "Exports weekly campaign configuration as JSON.";
    public override string Help => "wm.config.export <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine(_weekly.ExportConfig(args[0]));
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmStopCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.stop";
    public override string Description => "Stops the active weekly campaign without deleting config or snapshots.";
    public override string Help => "wm.stop <setId>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (_weekly.TryStop(args[0], out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmCancelCommand : LocalizedEntityCommands
{
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.cancel";
    public override string Description => "Cancels weekly mode and returns to normal round selection.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError("wm.cancel");
            return;
        }

        if (_weekly.TryCancel(out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}

internal static class WeeklyModeCommandParsing
{
    public static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "on":
                result = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
