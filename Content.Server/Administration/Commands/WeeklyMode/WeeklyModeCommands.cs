using System.Linq;
using Content.Server.WeeklyMode.Systems;
using Content.Shared.Administration;
using Content.Shared.Maps;
using Content.Shared.Roles;
using Content.Shared.WeeklyMode;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration.Commands.WeeklyMode;

[AdminCommand(AdminFlags.Round | AdminFlags.Server)]
public sealed class WmSetCreateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly WeeklyModeSystem _weekly = default!;

    public override string Command => "wm.set.create";
    public override string Description => "Creates a weekly mode set.";
    public override string Help => "wm.set.create <setId> <baseMapPrototype> [displayName]";

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
            2 => CompletionResult.FromHintOptions(
                _prototype.EnumeratePrototypes<GameMapPrototype>()
                    .Select(p => new CompletionOption(p.ID, p.MapName))
                    .OrderBy(p => p.Value),
                "Base map prototype"),
            3 => CompletionResult.FromHint("Display name"),
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
