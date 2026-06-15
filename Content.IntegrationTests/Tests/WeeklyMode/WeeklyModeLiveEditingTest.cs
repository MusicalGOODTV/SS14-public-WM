#nullable enable
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Research.Systems;
using Content.Server.Station.Systems;
using Content.Server.WeeklyMode.Systems;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.CCVar;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Station.Components;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.WeeklyMode;

[TestFixture]
public sealed class WeeklyModeLiveEditingTest : GameTest
{
    private const string EmptyMapPath = "/Maps/Test/empty.yml";
    private const string FirstRecipe = "PowerDrill";
    private const string SecondRecipe = "Welder";
    private const string CargoItem = "SheetSteel";

    [Test]
    public async Task AddUpdateAndRemoveResearchDuringActiveCampaignRefreshesRuntimeDatabase()
    {
        var setId = UniqueSetId();

        await UseIsolatedWeeklyRoot();

        await Server.WaitPost(() =>
        {
            var commandHost = Server.ResolveDependency<IConsoleHost>();
            var weekly = SEntMan.System<WeeklyModeSystem>();
            var research = SEntMan.System<ResearchSystem>();
            StartWeeklySet(weekly, setId);

            var databaseUid = SEntMan.SpawnEntity("WeeklyLiveResearchDatabase", MapCoordinates.Nullspace);
            var database = SEntMan.GetComponent<TechnologyDatabaseComponent>(databaseUid);

            commandHost.ExecuteCommand($"wm.tech {setId} industrial add WeeklyTools 100 1 {FirstRecipe}");

            Assert.Multiple(() =>
            {
                Assert.That(database.WeeklyModeOnly, Is.True);
                Assert.That(database.WeeklyTechnologies, Has.Count.EqualTo(1));
                Assert.That(database.WeeklyTechnologies[0].TechnologyId, Is.EqualTo("WeeklyTools"));
                Assert.That(database.WeeklyTechnologies[0].Branch, Is.EqualTo("Industrial"));
                Assert.That(database.WeeklyTechnologies[0].Cost, Is.EqualTo(100));
                Assert.That(database.WeeklyTechnologies[0].Tier, Is.EqualTo(1));
                Assert.That(database.WeeklyTechnologies[0].RecipeIds, Is.EqualTo(new[] { FirstRecipe }));
                Assert.That(research.GetAvailableWeeklyTechnologies(databaseUid, database).Select(x => x.TechnologyId),
                    Contains.Item("WeeklyTools"));
            });

            commandHost.ExecuteCommand($"wm.tech.update {setId} WeeklyTools service 75 2 {FirstRecipe} {SecondRecipe}");

            Assert.Multiple(() =>
            {
                Assert.That(database.WeeklyTechnologies, Has.Count.EqualTo(1));
                Assert.That(database.WeeklyTechnologies[0].Branch, Is.EqualTo("CivilianServices"));
                Assert.That(database.WeeklyTechnologies[0].Cost, Is.EqualTo(75));
                Assert.That(database.WeeklyTechnologies[0].Tier, Is.EqualTo(2));
                Assert.That(database.WeeklyTechnologies[0].RecipeIds, Is.EqualTo(new[] { FirstRecipe, SecondRecipe }));
                Assert.That(weekly.ExportConfig(setId), Does.Contain("WeeklyTools"));
                Assert.That(weekly.ExportConfig(setId), Does.Contain(SecondRecipe));
            });

            commandHost.ExecuteCommand($"wm.tech {setId} service remove WeeklyTools");

            Assert.Multiple(() =>
            {
                Assert.That(database.WeeklyModeOnly, Is.True);
                Assert.That(database.WeeklyTechnologies, Is.Empty);
                Assert.That(weekly.ListTechnologies(setId), Does.Contain("empty research tree"));
                Assert.That(weekly.ExportConfig(setId), Does.Not.Contain("WeeklyTools"));
            });
        });
    }

    [Test]
    public async Task PurchasedTechnologyRemovalIsRejectedWithoutForceAndCanBeForced()
    {
        var setId = UniqueSetId();

        await UseIsolatedWeeklyRoot();

        await Server.WaitPost(() =>
        {
            var commandHost = Server.ResolveDependency<IConsoleHost>();
            var weekly = SEntMan.System<WeeklyModeSystem>();
            var research = SEntMan.System<ResearchSystem>();
            StartWeeklySet(weekly, setId);

            var databaseUid = SEntMan.SpawnEntity("WeeklyLiveResearchDatabase", MapCoordinates.Nullspace);
            var database = SEntMan.GetComponent<TechnologyDatabaseComponent>(databaseUid);

            commandHost.ExecuteCommand($"wm.tech {setId} industrial add PurchasedWeeklyTools 100 1 {FirstRecipe}");

            var technology = database.WeeklyTechnologies.Single();
            research.AddWeeklyTechnology(databaseUid, technology, database);

            Assert.That(weekly.TryRemoveTechnology(setId, "industrial", "PurchasedWeeklyTools", false, out var message),
                Is.False);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("--force"));
                Assert.That(database.WeeklyUnlockedTechnologies, Contains.Item("PurchasedWeeklyTools"));
                Assert.That(database.UnlockedRecipes.Select(recipe => recipe.Id), Contains.Item(FirstRecipe));
                Assert.That(database.WeeklyTechnologies.Select(entry => entry.TechnologyId), Contains.Item("PurchasedWeeklyTools"));
            });

            commandHost.ExecuteCommand($"wm.tech {setId} industrial remove PurchasedWeeklyTools --force");

            Assert.Multiple(() =>
            {
                Assert.That(database.WeeklyUnlockedTechnologies, Does.Not.Contain("PurchasedWeeklyTools"));
                Assert.That(database.UnlockedRecipes.Select(recipe => recipe.Id), Does.Not.Contain(FirstRecipe));
                Assert.That(database.WeeklyTechnologies.Select(entry => entry.TechnologyId), Does.Not.Contain("PurchasedWeeklyTools"));
                Assert.That(weekly.ExportConfig(setId), Does.Not.Contain("PurchasedWeeklyTools"));
            });
        });
    }

    [Test]
    public async Task AddUpdateAndRemoveCargoDuringActiveCampaignRefreshesRuntimeCatalog()
    {
        var setId = UniqueSetId();

        await UseIsolatedWeeklyRoot();

        await Server.WaitPost(() =>
        {
            var commandHost = Server.ResolveDependency<IConsoleHost>();
            var weekly = SEntMan.System<WeeklyModeSystem>();
            var cargo = SEntMan.System<CargoSystem>();
            StartWeeklySet(weekly, setId);

            var console = CreateCargoConsole();

            commandHost.ExecuteCommand($"wm.cargo {setId} add WeeklySteel Materials 200 false 5 {CargoItem}");

            var products = cargo.GetAvailableWeeklyProducts((console.ConsoleUid, console.Console));
            AssertCargoUiState(console.ConsoleUid, "WeeklySteel", "Materials", 200, false, 5);
            Assert.Multiple(() =>
            {
                Assert.That(products, Has.Count.EqualTo(1));
                Assert.That(products[0].ProductId, Is.EqualTo("WeeklySteel"));
                Assert.That(products[0].Category, Is.EqualTo("Materials"));
                Assert.That(products[0].Cost, Is.EqualTo(200));
                Assert.That(products[0].Amount, Is.EqualTo(5));
                Assert.That(products[0].Boxed, Is.False);
            });

            commandHost.ExecuteCommand($"wm.cargo.update {setId} WeeklySteel Emergency 350 true 2 {CargoItem}");

            products = cargo.GetAvailableWeeklyProducts((console.ConsoleUid, console.Console));
            AssertCargoUiState(console.ConsoleUid, "WeeklySteel", "Emergency", 350, true, 2);
            Assert.Multiple(() =>
            {
                Assert.That(products, Has.Count.EqualTo(1));
                Assert.That(products[0].Category, Is.EqualTo("Emergency"));
                Assert.That(products[0].Cost, Is.EqualTo(350));
                Assert.That(products[0].Amount, Is.EqualTo(2));
                Assert.That(products[0].Boxed, Is.True);
                Assert.That(weekly.ExportConfig(setId), Does.Contain("\"WeeklySteel\""));
            });

            Assert.That(weekly.TryStop(setId, out var message), Is.True, message);
            Assert.That(weekly.TryStart(setId, null, "integration-test-restart", out message), Is.True, message);
            Assert.That(weekly.TryGetActiveWeeklyCargoProducts(out var restartedProducts), Is.True);
            var restartedProduct = restartedProducts.Single(product => product.ProductId == "WeeklySteel");
            Assert.Multiple(() =>
            {
                Assert.That(restartedProduct.Category, Is.EqualTo("Emergency"));
                Assert.That(restartedProduct.Cost, Is.EqualTo(350));
                Assert.That(restartedProduct.Amount, Is.EqualTo(2));
                Assert.That(restartedProduct.Boxed, Is.True);
            });

            commandHost.ExecuteCommand($"wm.cargo {setId} remove WeeklySteel");

            Assert.Multiple(() =>
            {
                Assert.That(cargo.GetAvailableWeeklyProducts((console.ConsoleUid, console.Console)), Is.Empty);
                AssertCargoUiStateDoesNotContain(console.ConsoleUid, "WeeklySteel");
                Assert.That(weekly.ListCargoProducts(setId), Does.Contain("empty cargo catalog"));
            });
        });
    }

    [Test]
    public async Task ExistingWeeklyCargoOrderSurvivesProductRemoval()
    {
        var setId = UniqueSetId();

        await UseIsolatedWeeklyRoot();

        await Server.WaitPost(() =>
        {
            var commandHost = Server.ResolveDependency<IConsoleHost>();
            var weekly = SEntMan.System<WeeklyModeSystem>();
            var cargo = SEntMan.System<CargoSystem>();
            StartWeeklySet(weekly, setId);

            var console = CreateCargoConsole();
            var orderDatabase = console.OrderDatabase;

            commandHost.ExecuteCommand($"wm.cargo {setId} add QueuedWeeklySteel Materials 200 false 5 {CargoItem}");
            Assert.That(weekly.TryGetActiveWeeklyCargoProduct("QueuedWeeklySteel", out var product), Is.True);

            var account = new ProtoId<CargoAccountPrototype>("Cargo");
            var order = new CargoOrderData(1, product, 1, "Test", "Queue survives product removal", account);
            order.Approved = true;
            orderDatabase.Orders[account].Add(order);

            commandHost.ExecuteCommand($"wm.cargo {setId} remove QueuedWeeklySteel");

            Assert.Multiple(() =>
            {
                Assert.That(cargo.GetAvailableWeeklyProducts((console.ConsoleUid, console.Console)), Is.Empty);
                Assert.That(orderDatabase.Orders[account], Has.Count.EqualTo(1));
                Assert.That(orderDatabase.Orders[account][0].IsWeeklyProduct, Is.True);
                Assert.That(orderDatabase.Orders[account][0].WeeklyProduct.ProductId, Is.EqualTo("QueuedWeeklySteel"));
                Assert.That(orderDatabase.Orders[account][0].WeeklyProduct.ItemPrototype, Is.EqualTo(CargoItem));
            });
        });
    }

    private async Task UseIsolatedWeeklyRoot()
    {
        await OverrideCVar(Side.Server, CCVars.WeeklyModeDataRoot, $"/weekly-mode-tests/{Guid.NewGuid():N}");
    }

    private static string UniqueSetId()
    {
        return $"live-edit-{Guid.NewGuid():N}";
    }

    private void StartWeeklySet(WeeklyModeSystem weekly, string setId)
    {
        Assert.That(weekly.TryCreateSet(setId, EmptyMapPath, "Live edit test", out var message), Is.True, message);
        Assert.That(weekly.TryStart(setId, null, "integration-test", out message), Is.True, message);
    }

    private CargoConsoleFixture CreateCargoConsole()
    {
        var station = SEntMan.SpawnEntity(null, MapCoordinates.Nullspace);
        SEntMan.EnsureComponent<StationDataComponent>(station);
        var orderDatabase = SEntMan.EnsureComponent<StationCargoOrderDatabaseComponent>(station);
        orderDatabase.Orders[new ProtoId<CargoAccountPrototype>("Cargo")] = [];

        var console = SEntMan.SpawnEntity("WeeklyLiveCargoConsole", MapCoordinates.Nullspace);
        var consoleComponent = SEntMan.GetComponent<CargoOrderConsoleComponent>(console);

        var tracker = SEntMan.EnsureComponent<StationTrackerComponent>(console);
        var stationSystem = SEntMan.System<StationSystem>();
        stationSystem.SetStation((console, tracker), station);

        return new CargoConsoleFixture(station, console, consoleComponent, orderDatabase);
    }

    private void AssertCargoUiState(EntityUid consoleUid, string productId, string category, int cost, bool boxed, int amount)
    {
        var ui = SEntMan.System<SharedUserInterfaceSystem>();
        Assert.That(ui.TryGetUiState<CargoConsoleInterfaceState>(consoleUid, CargoConsoleUiKey.Orders, out var state),
            Is.True,
            "Cargo order console did not receive a UI state update.");

        var product = state!.WeeklyProducts.Single(product => product.ProductId == productId);
        Assert.Multiple(() =>
        {
            Assert.That(product.Category, Is.EqualTo(category));
            Assert.That(product.Cost, Is.EqualTo(cost));
            Assert.That(product.Boxed, Is.EqualTo(boxed));
            Assert.That(product.Amount, Is.EqualTo(amount));
        });
    }

    private void AssertCargoUiStateDoesNotContain(EntityUid consoleUid, string productId)
    {
        var ui = SEntMan.System<SharedUserInterfaceSystem>();
        Assert.That(ui.TryGetUiState<CargoConsoleInterfaceState>(consoleUid, CargoConsoleUiKey.Orders, out var state),
            Is.True,
            "Cargo order console did not receive a UI state update.");

        Assert.That(state!.WeeklyProducts.Select(product => product.ProductId), Does.Not.Contain(productId));
    }

    private readonly record struct CargoConsoleFixture(
        EntityUid Station,
        EntityUid ConsoleUid,
        CargoOrderConsoleComponent Console,
        StationCargoOrderDatabaseComponent OrderDatabase);

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: WeeklyLiveResearchDatabase
  components:
  - type: TechnologyDatabase
    supportedDisciplines:
    - Industrial
    - CivilianServices

- type: entity
  id: WeeklyLiveCargoConsole
  components:
  - type: CargoOrderConsole
  - type: StationTracker
  - type: UserInterface
    interfaces:
      enum.CargoConsoleUiKey.Orders:
        type: CargoOrderConsoleBoundUserInterface
";
}
