using System.Linq;
using Content.Shared.Database;
using Content.Shared.Research;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Server.Research.Systems;

public sealed partial class ResearchSystem
{
    /// <summary>
    /// Syncs the primary entity's database to that of the secondary entity's database.
    /// </summary>
    public void Sync(EntityUid primaryUid, EntityUid otherUid, TechnologyDatabaseComponent? primaryDb = null, TechnologyDatabaseComponent? otherDb = null)
    {
        if (!Resolve(primaryUid, ref primaryDb) || !Resolve(otherUid, ref otherDb))
            return;

        primaryDb.MainDiscipline = otherDb.MainDiscipline;
        primaryDb.CurrentTechnologyCards = otherDb.CurrentTechnologyCards;
        primaryDb.SupportedDisciplines = otherDb.SupportedDisciplines;
        primaryDb.WeeklyModeOnly = otherDb.WeeklyModeOnly;
        primaryDb.WeeklyAllowedTechnologies = otherDb.WeeklyAllowedTechnologies;
        primaryDb.WeeklyTechnologies = otherDb.WeeklyTechnologies;
        primaryDb.UnlockedTechnologies = otherDb.UnlockedTechnologies;
        primaryDb.WeeklyUnlockedTechnologies = otherDb.WeeklyUnlockedTechnologies;
        primaryDb.UnlockedRecipes = otherDb.UnlockedRecipes;

        Dirty(primaryUid, primaryDb);

        var ev = new TechnologyDatabaseSynchronizedEvent();
        RaiseLocalEvent(primaryUid, ref ev);
    }

    /// <summary>
    ///     If there's a research client component attached to the owner entity,
    ///     and the research client is connected to a research server, this method
    ///     syncs against the research server, and the server against the local database.
    /// </summary>
    /// <returns>Whether it could sync or not</returns>
    public void SyncClientWithServer(EntityUid uid, TechnologyDatabaseComponent? databaseComponent = null, ResearchClientComponent? clientComponent = null)
    {
        if (!Resolve(uid, ref databaseComponent, ref clientComponent, false))
            return;

        if (!TryComp<TechnologyDatabaseComponent>(clientComponent.Server, out var serverDatabase))
            return;

        Sync(uid, clientComponent.Server.Value, databaseComponent, serverDatabase);
    }

    public void SetWeeklyModeOverlay(
        EntityUid uid,
        bool enabled,
        IReadOnlyList<WeeklyTechnologyData> weeklyTechnologies,
        bool clearUnlocked,
        TechnologyDatabaseComponent? databaseComponent = null)
    {
        if (!Resolve(uid, ref databaseComponent, false))
            return;

        var previousWeeklyTechnologies = databaseComponent.WeeklyTechnologies;
        databaseComponent.WeeklyModeOnly = enabled;
        databaseComponent.WeeklyTechnologies = enabled ? weeklyTechnologies.ToList() : new List<WeeklyTechnologyData>();
        databaseComponent.WeeklyAllowedTechnologies = enabled
            ? weeklyTechnologies
                .Where(technology => PrototypeManager.HasIndex<TechnologyPrototype>(technology.TechnologyId))
                .Select(technology => new ProtoId<TechnologyPrototype>(technology.TechnologyId))
                .ToList()
            : new List<ProtoId<TechnologyPrototype>>();

        var allowed = weeklyTechnologies.Select(technology => technology.TechnologyId).ToHashSet(StringComparer.Ordinal);
        databaseComponent.WeeklyUnlockedTechnologies.RemoveAll(id => !allowed.Contains(id));

        if (!enabled)
        {
            var normalUnlockedRecipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unlocked in databaseComponent.UnlockedTechnologies)
            {
                if (!PrototypeManager.TryIndex<TechnologyPrototype>(unlocked, out var technology))
                    continue;

                foreach (var recipe in technology.RecipeUnlocks)
                    normalUnlockedRecipes.Add(recipe);
            }

            foreach (var weeklyRecipe in previousWeeklyTechnologies.SelectMany(technology => technology.RecipeIds).Distinct(StringComparer.Ordinal))
            {
                if (!normalUnlockedRecipes.Contains(weeklyRecipe))
                    databaseComponent.UnlockedRecipes.Remove(weeklyRecipe);
            }

            databaseComponent.WeeklyUnlockedTechnologies.Clear();
        }
        else if (clearUnlocked)
        {
            var allowedPrototypes = databaseComponent.WeeklyAllowedTechnologies.ToHashSet();
            databaseComponent.UnlockedTechnologies.RemoveAll(id => !allowedPrototypes.Contains(id));

            var weeklyRecipesToKeep = databaseComponent.WeeklyTechnologies
                .Where(technology => databaseComponent.WeeklyUnlockedTechnologies.Contains(technology.TechnologyId))
                .SelectMany(technology => technology.RecipeIds)
                .ToHashSet(StringComparer.Ordinal);
            databaseComponent.UnlockedRecipes.RemoveAll(recipe => !weeklyRecipesToKeep.Contains(recipe));

            foreach (var recipe in weeklyRecipesToKeep)
            {
                if (!databaseComponent.UnlockedRecipes.Contains(recipe))
                    databaseComponent.UnlockedRecipes.Add(recipe);
            }
        }

        UpdateTechnologyCards(uid, databaseComponent);
    }

    /// <summary>
    /// Tries to add a technology to a database, checking if it is able to
    /// </summary>
    /// <returns>If the technology was successfully added</returns>
    public bool UnlockTechnology(EntityUid client,
        string prototypeid,
        EntityUid user,
        ResearchClientComponent? component = null,
        TechnologyDatabaseComponent? clientDatabase = null)
    {
        if (Resolve(client, ref component, ref clientDatabase, false) && clientDatabase.WeeklyModeOnly)
            return UnlockWeeklyTechnology(client, prototypeid, user, component, clientDatabase);

        if (!PrototypeManager.TryIndex<TechnologyPrototype>(prototypeid, out var prototype))
            return false;

        return UnlockTechnology(client, prototype, user, component, clientDatabase);
    }

    /// <summary>
    /// Tries to add a technology to a database, checking if it is able to
    /// </summary>
    /// <returns>If the technology was successfully added</returns>
    public bool UnlockTechnology(EntityUid client,
        TechnologyPrototype prototype,
        EntityUid user,
        ResearchClientComponent? component = null,
        TechnologyDatabaseComponent? clientDatabase = null)
    {
        if (!Resolve(client, ref component, ref clientDatabase, false))
            return false;

        if (!TryGetClientServer(client, out var serverEnt, out _, component))
            return false;

        if (!CanServerUnlockTechnology(client, prototype, clientDatabase, component))
            return false;

        AddTechnology(serverEnt.Value, prototype);
        TrySetMainDiscipline(prototype, serverEnt.Value);
        ModifyServerPoints(serverEnt.Value, -prototype.Cost);
        UpdateTechnologyCards(serverEnt.Value);

        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(user):player} unlocked {prototype.ID} (discipline: {prototype.Discipline}, tier: {prototype.Tier}) at {ToPrettyString(client)}, for server {ToPrettyString(serverEnt.Value)}.");
        return true;
    }

    public bool UnlockWeeklyTechnology(EntityUid client,
        string technologyId,
        EntityUid user,
        ResearchClientComponent? component = null,
        TechnologyDatabaseComponent? clientDatabase = null)
    {
        if (!Resolve(client, ref component, ref clientDatabase, false))
            return false;

        if (!TryGetClientServer(client, out var serverEnt, out var serverComp, component))
            return false;

        if (!TryGetWeeklyTechnology(clientDatabase, technologyId, out var technology))
            return false;

        if (!IsWeeklyTechnologyAvailable(clientDatabase, technology))
            return false;

        if (technology.Cost > serverComp.Points)
            return false;

        AddWeeklyTechnology(serverEnt.Value, technology);
        TrySetWeeklyMainDiscipline(technology, serverEnt.Value);
        ModifyServerPoints(serverEnt.Value, -technology.Cost);
        UpdateTechnologyCards(serverEnt.Value);

        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(user):player} unlocked weekly technology {technology.TechnologyId} (branch: {technology.Branch}, tier: {technology.Tier}) at {ToPrettyString(client)}, for server {ToPrettyString(serverEnt.Value)}.");
        return true;
    }

    /// <summary>
    ///     Adds a technology to the database without checking if it could be unlocked.
    /// </summary>
    [PublicAPI]
    public void AddTechnology(EntityUid uid, string technology, TechnologyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!PrototypeManager.TryIndex<TechnologyPrototype>(technology, out var prototype))
            return;
        AddTechnology(uid, prototype, component);
    }

    /// <summary>
    ///     Adds a technology to the database without checking if it could be unlocked.
    /// </summary>
    public void AddTechnology(EntityUid uid, TechnologyPrototype technology, TechnologyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        //todo this needs to support some other stuff, too
        foreach (var generic in technology.GenericUnlocks)
        {
            if (generic.PurchaseEvent != null)
                RaiseLocalEvent(generic.PurchaseEvent);
        }

        component.UnlockedTechnologies.Add(technology.ID);
        var addedRecipes = new List<string>();
        foreach (var unlock in technology.RecipeUnlocks)
        {
            if (component.UnlockedRecipes.Contains(unlock))
                continue;
            component.UnlockedRecipes.Add(unlock);
            addedRecipes.Add(unlock);
        }
        Dirty(uid, component);

        var ev = new TechnologyDatabaseModifiedEvent(addedRecipes);
        RaiseLocalEvent(uid, ref ev);
    }

    public void AddWeeklyTechnology(EntityUid uid, WeeklyTechnologyData technology, TechnologyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (component.WeeklyUnlockedTechnologies.Contains(technology.TechnologyId))
            return;

        component.WeeklyUnlockedTechnologies.Add(technology.TechnologyId);
        var addedRecipes = new List<string>();
        foreach (var unlock in technology.RecipeIds)
        {
            if (component.UnlockedRecipes.Contains(unlock))
                continue;

            component.UnlockedRecipes.Add(unlock);
            addedRecipes.Add(unlock);
        }

        Dirty(uid, component);

        var ev = new TechnologyDatabaseModifiedEvent(addedRecipes);
        RaiseLocalEvent(uid, ref ev);
    }

    /// <summary>
    ///     Returns whether a technology can be unlocked on this database,
    ///     taking parent technologies into account.
    /// </summary>
    /// <returns>Whether it could be unlocked or not</returns>
    public bool CanServerUnlockTechnology(EntityUid uid,
        TechnologyPrototype technology,
        TechnologyDatabaseComponent? database = null,
        ResearchClientComponent? client = null)
    {

        if (!Resolve(uid, ref client, ref database, false))
            return false;

        if (!TryGetClientServer(uid, out _, out var serverComp, client))
            return false;

        if (!IsTechnologyAvailable(database, technology))
            return false;

        if (technology.Cost > serverComp.Points)
            return false;

        return true;
    }

    public bool TryGetWeeklyTechnology(TechnologyDatabaseComponent database, string technologyId, out WeeklyTechnologyData technology)
    {
        foreach (var candidate in database.WeeklyTechnologies)
        {
            if (!string.Equals(candidate.TechnologyId, technologyId, StringComparison.Ordinal))
                continue;

            technology = candidate;
            return true;
        }

        technology = default;
        return false;
    }

    public void TrySetWeeklyMainDiscipline(WeeklyTechnologyData technology, EntityUid uid, TechnologyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var discipline = PrototypeManager.Index<TechDisciplinePrototype>(technology.Branch);
        if (technology.Tier < discipline.LockoutTier)
            return;

        component.MainDiscipline = technology.Branch;
        Dirty(uid, component);

        var ev = new TechnologyDatabaseModifiedEvent();
        RaiseLocalEvent(uid, ref ev);
    }

    private void OnDatabaseRegistrationChanged(EntityUid uid, TechnologyDatabaseComponent component, ref ResearchRegistrationChangedEvent args)
    {
        if (args.Server != null)
            return;
        component.MainDiscipline = null;
        component.CurrentTechnologyCards = new List<string>();
        component.SupportedDisciplines = new List<ProtoId<TechDisciplinePrototype>>();
        component.WeeklyModeOnly = false;
        component.WeeklyAllowedTechnologies = new List<ProtoId<TechnologyPrototype>>();
        component.WeeklyTechnologies = new List<WeeklyTechnologyData>();
        component.UnlockedTechnologies = new List<ProtoId<TechnologyPrototype>>();
        component.WeeklyUnlockedTechnologies = new List<string>();
        component.UnlockedRecipes = new List<ProtoId<LatheRecipePrototype>>();
        Dirty(uid, component);
    }
}
