using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using swiftlyS2_countryflags.Models;
using swiftlyS2_countryflags.Services;
using System.Collections.Concurrent;

namespace swiftlyS2_countryflags;

[PluginMetadata(
    Id = "swiftlyS2_countryflags",
    Version = Constants.Version,
    Name = "SwiftlyS2 Country Flags",
    Author = "zhw1nq, Mvkweb",
    Description =
        "Displays country flags on the scoreboard based on player's IP location. [For SwiftlyS2]"
)]
public sealed partial class CountryFlagsPlugin : BasePlugin
{
    private CountryFlagsConfig _config = new();

    private GeoIpService? _geoIpService;
    private PlayerDataService? _dataService;

    private readonly ConcurrentDictionary<ulong, byte>
        _processingPlayers = new();

    private CancellationTokenSource? _saveTimerToken;

    public CountryFlagsPlugin(ISwiftlyCore core)
        : base(core)
    {
    }

    private void LogDebug(
        string message,
        params object[] args)
    {
        if (_config.Debug)
        {
            Core.Logger.LogDebug(
                message,
                args);
        }
    }

    public override void ConfigureSharedInterface(
        IInterfaceManager interfaceManager)
    {
    }

    public override void UseSharedInterface(
        IInterfaceManager interfaceManager)
    {
    }

    public override void Load(bool hotReload)
    {
        try
        {
            LoadConfiguration();

            if (!_config.Enabled)
            {
                Core.Logger.LogWarning(
                    "[CountryFlags] Plugin is disabled in config");

                return;
            }

            InitializeServices();
            RegisterEvents();

            Core.Logger.LogInformation(
                "[CountryFlags] v{Version} loaded successfully",
                Constants.Version);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                "[CountryFlags] Failed to load plugin: {Error}",
                ex.Message);

            Core.Logger.LogError(
                "[CountryFlags] Stack trace: {Stack}",
                ex.StackTrace ?? "N/A");
        }
    }

    public override void Unload()
    {
        UnregisterEvents();

        _saveTimerToken?.Cancel();
        _saveTimerToken = null;

        _dataService?.SaveIfDirty();

        _geoIpService?.Dispose();
        _dataService?.Dispose();

        _processingPlayers.Clear();
    }

    // ---------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------

    private void LoadConfiguration()
    {
        Core.Configuration
            .InitializeWithTemplate(
                "config.jsonc",
                "config.template.jsonc")
            .Configure(builder =>
            {
                builder.AddJsonFile(
                    "config.jsonc",
                    optional: false,
                    reloadOnChange: true);
            });

        var services = new ServiceCollection();

        services
            .AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<CountryFlagsConfig>()
            .BindConfiguration("CountryFlags");

        using var provider =
            services.BuildServiceProvider();

        _config =
            provider
                .GetRequiredService<
                    IOptionsMonitor<CountryFlagsConfig>>()
                .CurrentValue;

        if (_config.CountryBadges.Count == 0)
        {
            Core.Logger.LogWarning(
                "[CountryFlags] No country badges configured!");
        }
    }

    // ---------------------------------------------------------
    // Services
    // ---------------------------------------------------------

    private void InitializeServices()
    {
        var geoDbPath =
            Path.Combine(
                Core.PluginDataDirectory,
                "GeoLite2-Country.mmdb");

        _geoIpService =
            new GeoIpService(
                Core,
                geoDbPath)
            {
                DebugEnabled = _config.Debug
            };

        if (!_geoIpService.Initialize())
        {
            Core.Logger.LogWarning(
                "[CountryFlags] GeoIP not available. " +
                "Plugin will use cached data only.");
        }

        var dataPath =
            Path.Combine(
                Core.PluginDataDirectory,
                "countryflags_cache.json");

        _dataService =
            new PlayerDataService(dataPath);

        _ = LoadCachedDataAsync();

        if (_config.AutoSaveIntervalSeconds > 0)
        {
            _saveTimerToken =
                Core.Scheduler.RepeatBySeconds(
                    _config.AutoSaveIntervalSeconds,
                    () => _dataService?.SaveIfDirty());
        }
    }

    private async Task LoadCachedDataAsync()
    {
        try
        {
            await _dataService!
                .LoadAsync()
                .ConfigureAwait(false);

            Core.Logger.LogInformation(
                "[CountryFlags] Loaded {Count} cached player records",
                _dataService.Count);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(
                "[CountryFlags] Error loading cache: {Error}",
                ex.Message);
        }
    }

    // ---------------------------------------------------------
    // Events
    // ---------------------------------------------------------

    private void RegisterEvents()
    {
        // No OnTick subscription.
        // Badge updates are event-driven only.
    }

    private void UnregisterEvents()
    {
        // Nothing to unregister.
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnectFull(
        EventPlayerConnectFull @event)
    {
        var player =
            @event.Accessor.GetPlayer("userid");

        if (player == null ||
            player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        var steamId = player.SteamID;

        var playerData =
            _dataService!.GetOrCreate(
                steamId,
                _config.DefaultStatus);

        if (playerData.ShowFlag &&
            !string.IsNullOrEmpty(playerData.CountryCode))
        {
            ApplyCountryBadge(
                player,
                playerData);

            return HookResult.Continue;
        }

        if (ShouldFetchCountry(playerData))
        {
            FetchPlayerCountry(
                player,
                playerData);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDisconnect(
        EventPlayerDisconnect @event)
    {
        var player =
            @event.Accessor.GetPlayer("userid");

        if (player != null &&
            !player.IsFakeClient)
        {
            _processingPlayers.TryRemove(
                player.SteamID,
                out _);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(
        EventPlayerSpawn @event)
    {
        var player =
            @event.Accessor.GetPlayer("userid");

        if (player == null ||
            player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        if (!_dataService!.TryGetPlayer(
                player.SteamID,
                out var playerData) ||
            playerData == null)
        {
            return HookResult.Continue;
        }

        if (!playerData.ShowFlag ||
            string.IsNullOrEmpty(playerData.CountryCode))
        {
            return HookResult.Continue;
        }

        // A spawn can rebuild the player's inventory state.
        // Force one badge application after spawn.
        playerData.AppliedBadgeId = 0;

        ApplyCountryBadge(
            player,
            playerData);

        return HookResult.Continue;
    }

    // ---------------------------------------------------------
    // Commands
    // ---------------------------------------------------------

    [Command("flag")]
    [CommandAlias("cf")]
    public void OnFlagCommand(
        ICommandContext context)
    {
        if (!context.IsSentByPlayer ||
            context.Sender == null)
        {
            return;
        }

        var player = context.Sender;

        if (!_config.EnableToggleCommand)
        {
            player.SendChat(
                $"{Core.Localizer["prefix"]}" +
                $"{Core.Localizer["command_disabled"]}");

            return;
        }

        HandleFlagToggle(player);
    }

    private void HandleFlagToggle(
        IPlayer player)
    {
        var steamId = player.SteamID;

        var playerData =
            _dataService!.GetOrCreate(
                steamId,
                _config.DefaultStatus);

        playerData.ShowFlag =
            !playerData.ShowFlag;

        _dataService.MarkDirty();

        if (playerData.ShowFlag)
        {
            player.SendChat(
                $"{Core.Localizer["prefix"]}" +
                $"{Core.Localizer["flag_enabled"]}");

            if (!string.IsNullOrEmpty(
                    playerData.CountryCode))
            {
                ApplyCountryBadge(
                    player,
                    playerData);

                player.SendChat(
                    $"{Core.Localizer["prefix"]}" +
                    $"{Core.Localizer["country_info",
                        playerData.CountryCode]}");
            }
            else if (ShouldFetchCountry(playerData))
            {
                player.SendChat(
                    $"{Core.Localizer["prefix"]}" +
                    $"{Core.Localizer["detecting"]}");

                FetchPlayerCountry(
                    player,
                    playerData);
            }
        }
        else
        {
            player.SendChat(
                $"{Core.Localizer["prefix"]}" +
                $"{Core.Localizer["flag_disabled"]}");

            RemoveCountryBadge(player);
        }
    }

    // ---------------------------------------------------------
    // Country Detection
    // ---------------------------------------------------------

    private bool ShouldFetchCountry(
        PlayerData playerData)
    {
        return string.IsNullOrEmpty(
                   playerData.CountryCode)
               ||
               DateTime.UtcNow -
                   playerData.LastFetch
                   >
                   TimeSpan.FromHours(
                       _config.CacheExpiryHours);
    }

    private void FetchPlayerCountry(
        IPlayer player,
        PlayerData playerData)
    {
        var steamId = player.SteamID;

        if (!_processingPlayers.TryAdd(
                steamId,
                0))
        {
            return;
        }

        try
        {
            var countryCode =
                _geoIpService?.GetCountryCode(
                    player.IPAddress);

            if (string.IsNullOrEmpty(countryCode))
                return;

            // Country hasn't changed.
            // Only refresh the cache timestamp.
            if (string.Equals(
                    playerData.CountryCode,
                    countryCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                playerData.LastFetch =
                    DateTime.UtcNow;

                _dataService!.MarkDirty();

                return;
            }

            playerData.CountryCode =
                countryCode;

            playerData.LastFetch =
                DateTime.UtcNow;

            // The country changed, therefore any previously
            // applied badge is no longer considered valid.
            playerData.AppliedBadgeId = 0;

            _dataService!.MarkDirty();

            LogDebug(
                "[CountryFlags] Player {SteamId} country: {Country}",
                steamId,
                countryCode);

            // Apply immediately if the player has flags enabled.
            if (playerData.ShowFlag &&
                player.IsValid)
            {
                ApplyCountryBadge(
                    player,
                    playerData);
            }
        }
        catch (Exception ex)
        {
            LogDebug(
                "[CountryFlags] Error fetching country for {SteamId}: {Error}",
                steamId,
                ex.Message);
        }
        finally
        {
            _processingPlayers.TryRemove(
                steamId,
                out _);
        }
    }

    // ---------------------------------------------------------
    // Badge Management
    // ---------------------------------------------------------

    private void ApplyCountryBadge(
        IPlayer player,
        PlayerData playerData)
    {
        try
        {
            if (!player.IsValid)
                return;

            if (!playerData.ShowFlag)
                return;

            var countryCode =
                playerData.CountryCode;

            if (string.IsNullOrEmpty(countryCode))
                return;

            var controller =
                player.Controller;

            var inventoryServices =
                controller?.InventoryServices;

            if (inventoryServices == null)
                return;

            var badgeId =
                _config.CountryBadges.TryGetValue(
                    countryCode,
                    out var configuredId)
                    ? configuredId
                    : _config.DefaultBadgeId;

            if (badgeId <= 0)
                return;

            // The correct badge is already applied.
            // Do absolutely nothing.
            if (playerData.AppliedBadgeId == badgeId)
                return;

            // Clear existing ranks.
            for (var i = 0; i < 6; i++)
            {
                inventoryServices.Rank[i] =
                    (MedalRank_t)0;
            }

            inventoryServices.Rank[
                Constants.BadgeSlotIndex] =
                (MedalRank_t)badgeId;

            controller.InventoryServicesUpdated();

            // Remember what was actually applied.
            playerData.AppliedBadgeId =
                badgeId;
        }
        catch (Exception ex)
        {
            LogDebug(
                "[CountryFlags] Error applying badge: {Error}",
                ex.Message);
        }
    }

    private void RemoveCountryBadge(
        IPlayer player)
    {
        try
        {
            if (!player.IsValid)
                return;

            var playerData =
                _dataService?.GetOrCreate(
                    player.SteamID,
                    _config.DefaultStatus);

            if (playerData != null &&
                playerData.AppliedBadgeId == 0)
            {
                return;
            }

            var controller =
                player.Controller;

            var inventoryServices =
                controller?.InventoryServices;

            if (inventoryServices == null)
                return;

            inventoryServices.Rank[
                Constants.BadgeSlotIndex] =
                (MedalRank_t)0;

            controller.InventoryServicesUpdated();

            if (playerData != null)
            {
                playerData.AppliedBadgeId = 0;
            }
        }
        catch (Exception ex)
        {
            LogDebug(
                "[CountryFlags] Error removing badge: {Error}",
                ex.Message);
        }
    }
}
