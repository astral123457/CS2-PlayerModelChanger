﻿using CounterStrikeSharp.API.Core;

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using System.Drawing;
using CounterStrikeSharp.API.Modules.Config;
using Microsoft.Extensions.Logging;
namespace PlayerModelChanger;

public partial class PlayerModelChanger : BasePlugin, IPluginConfig<ModelConfig>
{
    public override string ModuleName => "Player Model Changer";
    public override string ModuleVersion => "1.7.2";

    public override string ModuleAuthor => "samyyc";
    public required ModelConfig Config { get; set; }
    public required ModelService Service { get; set; }

    public required DefaultModelManager DefaultModelManager { get; set; }

    private static PlayerModelChanger? _Instance { get; set; }

    public bool Enable = true;

    public override void Load(bool hotReload)
    {
        _Instance = this;
        IStorage? Storage = null;
        switch (Config.StorageType)
        {
            case "sqlite":
                Storage = new SqliteStorage(ModuleDirectory);
                break;
            case "mysql":
                Storage = new MySQLStorage(Config.MySQLIP, Config.MySQLPort, Config.MySQLUser, Config.MySQLPassword, Config.MySQLDatabase, Config.MySQLTable);
                break;
        };
        if (Storage == null)
        {
            throw new Exception("[PlayerModelChanger] Failed to initialize storage. Please check your config");
        }
        DefaultModelManager = new DefaultModelManager();
        this.Service = new ModelService(Config, Storage, Localizer, DefaultModelManager);
        DefaultModelManager.ReloadConfig(ModuleDirectory, Service);
        if (!Config.DisablePrecache)
        {
            RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheResource);
        }
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawnEvent);
        RegisterListener<Listeners.OnTick>(OnTick);

        Utils.InitializeLangPrefix();
        Logger.LogInformation($"Loaded {Service.GetModelCount()} model(s) successfully.");
    }

    private void PrecacheResource(ResourceManifest manifest)
    {
        foreach (var model in Service.GetAllModels())
        {
            Logger.LogInformation($"Precaching {model.Path}");
            manifest.AddResource(model.Path);
        }
    }

    public override void Unload(bool hotReload)
    {
        _Instance = null;
        RemoveListener<Listeners.OnServerPrecacheResources>(PrecacheResource);
        RemoveListener<Listeners.OnTick>(OnTick);
        DeregisterEventHandler<EventPlayerSpawn>(OnPlayerSpawnEvent);
        Logger.LogInformation("Unloaded successfully.");


    }

    public static PlayerModelChanger getInstance()
    {
        return _Instance!;
    }

    public void ReloadConfig()
    {
        var config = typeof(ConfigManager)
                   .GetMethod("Load")!
                   .MakeGenericMethod(typeof(ModelConfig))
                   .Invoke(null, new object[] { Path.GetFileName(ModuleDirectory) }) as IBasePluginConfig;
        OnConfigParsed((config as ModelConfig)!);
        Unload(true);
        Load(false);
        Service.ReloadConfig(ModuleDirectory, Config);
    }
    public void OnConfigParsed(ModelConfig config)
    {
        var availableStorageType = new[] { "sqlite", "mysql" };
        if (!availableStorageType.Contains(config.StorageType))
        {
            throw new Exception($"[PlayerModelChanger] Unknown storage type: {Config.StorageType}, available types: {string.Join(",", availableStorageType)}");
        }

        if (config.StorageType == "mysql")
        {
            if (config.MySQLIP == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MySQL_IP");
            }
            if (config.MySQLPort == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MYSQL_Port");
            }
            if (config.MySQLUser == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MYSQL_User");
            }
            if (config.MySQLPassword == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MYSQL_Password");
            }
            if (config.MySQLDatabase == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MySQL_Database");
            }
        }

        if (config.ModelForBots == null)
        {
            config.ModelForBots = new BotsConfig();
        }

        if (!new string[] { "chat", "centerhtml", "wasd", "interactive" }.Contains(config.MenuType.ToLower()))
        {
            throw new Exception($"[PlayerModelChanger] Unknown menu type: {config.MenuType}");
        }
        config.MenuType = config.MenuType.ToLower();
        for (int i = 0; i < config.Models.Count; i++)
        {
            var entry = config.Models.ElementAt(i);
            ModelService.InitializeModel(entry.Key, entry.Value);
            if (config.Models.Where(m => m.Value.Name == entry.Value.Name).Count() > 1)
            {
                throw new Exception($"[PlayerModelChanger] Found duplicated model name: {entry.Value.Name}");
            }
        }

        Config = config;
    }

    public void OnTick()
    {
        Inspection.UpdateCamera();
    }

    // from https://github.com/Challengermode/cm-cs2-defaultskins/
    [GameEventHandler]
    public HookResult OnPlayerSpawnEvent(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!Enable)
        {
            return HookResult.Continue;
        }

        if (@event == null)
        {
            return HookResult.Continue;
        }

        CCSPlayerController? player = @event.Userid;

        if (player == null
            || !player.IsValid)
        {
            return HookResult.Continue;
        }
        try
        {
            CsTeam team = (CsTeam)player.TeamNum;

            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist)
            {
                return HookResult.Continue;
            }

            if (player.IsBot)
            {
                string modelindex = team == CsTeam.Terrorist ? Config.ModelForBots.T : Config.ModelForBots.CT;
                if (modelindex == "")
                {
                    return HookResult.Continue;
                }
                var botmodel = Service.GetModel(modelindex);
                if (botmodel != null)
                {
                    SetModelNextServerFrame(player, botmodel.Path, botmodel.Disableleg);
                }
                else
                {
                    Server.NextFrame(() =>
                    {
                        var originalRender = player.Pawn.Value!.Render;
                        player.Pawn.Value.Render = Color.FromArgb(255, originalRender.R, originalRender.G, originalRender.B);
                    });
                }
            }

            if (player.AuthorizedSteamID == null)
            {
                return HookResult.Continue;
            }

            if (
            player.PlayerPawn == null
            || !player.PlayerPawn.IsValid
            || player.PlayerPawn.Value == null
            || !player.PlayerPawn.Value.IsValid
            )
            {
                return HookResult.Continue;
            }

            if (Config.AutoResyncCache)
            {
                Service.ResyncCache();
            }

            if (!Config.DisableAutoCheck)
            {
                var result = Service.CheckAndReplaceModel(player);

                if (result.Item1 && result.Item2)
                {
                    player.PrintToChat(Localizer["model.invalidreseted", Localizer["side.all"]]);
                }
                else if (result.Item1)
                {
                    player.PrintToChat(Localizer["model.invalidreseted", Localizer["side.t"]]);
                }
                else if (result.Item2)
                {
                    player.PrintToChat(Localizer["model.invalidreseted", Localizer["side.ct"]]);
                }
            }


            var model = Service.GetPlayerNowTeamModel(player);

            if (model != null)
            {
                SetModelNextServerFrame(player, model.Path, model.Disableleg);
            }
            else
            {
                Server.NextFrame(() =>
                {
                    var originalRender = player.PlayerPawn.Value.Render;
                    player.PlayerPawn.Value.Render = Color.FromArgb(255, originalRender.R, originalRender.G, originalRender.B);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogInformation("Could not set player model: {0}", ex);
        }

        return HookResult.Continue;
    }

    public void SetModelNextServerFrame(CCSPlayerController player, string model, bool disableleg)
    {
        Server.NextFrame(() =>
        {
            var pawn = player.Pawn.Value!;
            pawn.SetModel(model);
            var originalRender = pawn.Render;
            pawn.Render = Color.FromArgb(disableleg ? 254 : 255, originalRender.R, originalRender.G, originalRender.B);
        });
    }
}
