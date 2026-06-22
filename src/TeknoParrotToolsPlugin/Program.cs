using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HyperAI.Plugin.Common;
using HyperAI.Plugin.SocketIO;

namespace TeknoParrotToolsPlugin;

public static class TeknoParrotToolsPluginMain
{
    internal const string PluginId = "teknoparrot-tools";
    internal const string PluginName = "TeknoParrot Tools";
    internal const string WizardId = "teknoparrot-tools-setup";
    internal const string TeknoParrotSystemName = "Arcade (TeknoParrot)";
    internal const string TeknoParrotSystemReferenceId = "97d957bb-1490-4c1f-b698-08dd285234a8";
    internal const string TeknoParrotSystemDescription = "TeknoParrot is a software project designed to run select PC-based arcade titles on personal computers, acting as a compatibility or translation layer rather than a traditional hardware emulator. It aims to preserve arcade history and bring the arcade experience to the PC.";
    internal const string TeknoParrotLaunchCommand = "--profile=\"%rom.filename%.xml\" --startMinimized";
    internal const string TeknoParrotAllowedExtensions = "exe|xml|zip";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static TeknoParrotSettings settings = new();
    private static PluginSocketIOClient? socketClient;
    private static bool useSocketIO;
    private static int socketServerPort;

    public static async Task Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--version")
            {
                Console.WriteLine("0.1.0");
                return;
            }

            var socketPortArg = args.FirstOrDefault(arg => arg.StartsWith("--socket-port=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(socketPortArg) &&
                int.TryParse(socketPortArg["--socket-port=".Length..], out socketServerPort))
            {
                useSocketIO = true;
            }
            else
            {
                var envPort = Environment.GetEnvironmentVariable("HYPERHQ_SOCKET_PORT") ??
                    Environment.GetEnvironmentVariable("HYPERAI_SOCKET_PORT");

                if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out socketServerPort))
                {
                    useSocketIO = true;
                }
            }

            if (useSocketIO)
            {
                await RunSocketIOMode();
                return;
            }

            await RunStdinStdoutMode();
        }
        catch (Exception ex)
        {
            await LogErrorAsync($"Plugin main error: {ex.Message}");
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
        }
    }

    private static async Task RunSocketIOMode()
    {
        try
        {
            var pluginId = Environment.GetEnvironmentVariable("HYPERHQ_PLUGIN_ID") ?? PluginId;
            var authToken = GenerateAuthToken();
            socketClient = new PluginSocketIOClient(pluginId, authToken, socketServerPort);

            socketClient.EventReceived += async (eventType, data) =>
            {
                if (eventType == "dbConnected" && settings.AutoSyncOnDbConnect)
                {
                    await SyncGames(JsonDocument.Parse("{}").RootElement);
                }
            };

            socketClient.OnEvent("request", async data => await HandleSocketIORequest(data));
            await socketClient.ConnectAsync();

            var timeoutAt = DateTime.UtcNow.AddSeconds(30);
            while (!socketClient.IsAuthenticated && DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(100);
            }

            if (!socketClient.IsAuthenticated)
            {
                throw new InvalidOperationException("Failed to authenticate with HyperHQ within 30 seconds.");
            }

            await socketClient.SubscribeToEventsAsync(new[] { "dbConnected", "systemChanged" });
            await socketClient.UpdateStatusAsync("connected", "TeknoParrot Tools connected and ready");

            while (socketClient.IsConnected)
            {
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            await LogErrorAsync($"Socket.IO mode error: {ex.Message}");
            if (socketClient != null)
            {
                await socketClient.UpdateStatusAsync("error", ex.Message);
            }
        }
        finally
        {
            socketClient?.Dispose();
        }
    }

    private static async Task RunStdinStdoutMode()
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await ProcessMessage(line);
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    private static async Task HandleSocketIORequest(JsonElement data)
    {
        if (socketClient == null)
        {
            return;
        }

        if (!data.TryGetProperty("id", out var idElement) ||
            !data.TryGetProperty("method", out var methodElement))
        {
            await LogErrorAsync("Invalid request: missing id or method.");
            return;
        }

        var requestId = idElement.GetString();
        var method = methodElement.GetString();
        var requestData = data.TryGetProperty("data", out var dataElement)
            ? dataElement
            : JsonDocument.Parse("{}").RootElement;

        var response = await DispatchMethod(method, requestData);
        await socketClient.EmitAsync("response", new
        {
            id = requestId,
            type = "response",
            data = response,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    internal static async Task<object> ProcessMessage(string input)
    {
        try
        {
            input = input.TrimStart('\uFEFF');
            var message = JsonSerializer.Deserialize<PluginMessage>(input, JsonOptions);
            if (message == null || string.IsNullOrWhiteSpace(message.Method))
            {
                return new { error = "Invalid plugin message." };
            }

            return await DispatchMethod(message.Method, message.Data ?? JsonDocument.Parse("{}").RootElement);
        }
        catch (Exception ex)
        {
            await LogErrorAsync($"Message processing error: {ex.Message}");
            return new { error = ex.Message };
        }
    }

    private static Task<object> DispatchMethod(string? method, JsonElement data)
    {
        return method switch
        {
            "initialize" => Initialize(data),
            "updateSettings" => Initialize(data),
            "update_settings" => Initialize(data),
            "execute" => Execute(data),
            "getStatus" => GetStatus(),
            "get_status" => GetStatus(),
            "onboardingStepExecute" => OnboardingStepExecute(data),
            "onboarding/step-execute" => OnboardingStepExecute(data),
            "shutdown" => Shutdown(),
            _ => Task.FromResult<object>(new { error = $"Unknown method: {method}" })
        };
    }

    private static Task<object> Initialize(JsonElement data)
    {
        if (data.TryGetProperty("settings", out var settingsElement))
        {
            settings = MergeSettings(settings, settingsElement);
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            settings = MergeSettings(settings, data);
        }

        return GetStatus();
    }

    private static object PreviewRegisterGames(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        return TeknoParrotProfileScanner.RegisterGames(settings, dryRun: true);
    }

    private static object RegisterGames(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var backup = dryRun ? null : TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.RegisterGames(settings, dryRun);
        return result with { BackupPath = backup?.BackupPath };
    }

    private static object RegisterGamesForWizard(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var gamesRootPath = TeknoParrotProfileScanner.ResolveGamesRootPathForSettings(settings);
        if (string.IsNullOrWhiteSpace(gamesRootPath) || !Directory.Exists(gamesRootPath))
        {
            return new
            {
                success = true,
                skipped = true,
                statusMessage = "No games root folder configured; existing UserProfiles will be scanned next."
            };
        }

        return RegisterGames(data);
    }

    private static object RepairGamePaths(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var backup = dryRun ? null : TryBackupProfilesForMutation(settings);
        if (backup is { Success: false })
        {
            return new { success = false, error = backup.Error };
        }

        var result = TeknoParrotProfileScanner.RepairGamePaths(settings, dryRun);
        return result with { BackupPath = backup?.BackupPath };
    }

    private static async Task<object> Execute(JsonElement data)
    {
        var action = GetString(data, "action") ?? "get_status";
        var previousSettings = settings;
        settings = MergeSettings(settings, data);

        var response = action switch
        {
            "run_setup_wizard" => new { success = true, wizard_id = WizardId },
            "scan_profiles" => BuildScanResponse(TeknoParrotProfileScanner.Scan(settings)),
            "scan_games" => BuildScanResponse(TeknoParrotProfileScanner.Scan(settings)),
            "health_check" => BuildScanResponse(TeknoParrotProfileScanner.Scan(settings)),
            "get_status" => await GetStatus(),
            "status" => await GetStatus(),
            "preview_registration" => PreviewRegisterGames(data),
            "register_games" => RegisterGames(data),
            "repair_game_paths" => RepairGamePaths(data),
            "preview_sync" => await SyncGames(SetDryRun(data)),
            "sync_games" => await SyncGames(data),
            "backup_profiles" => BackupProfiles(settings),
            "restore_backup" => RestoreBackup(settings, data),
            "onboardingStepExecute" => await OnboardingStepExecute(data),
            _ => new { error = $"Unsupported action: {action}" }
        };

        settings = previousSettings.MergeWith(settings);
        return response;
    }

    private static Task<object> GetStatus()
    {
        var scan = TeknoParrotProfileScanner.Scan(settings);
        return Task.FromResult<object>(new
        {
            success = scan.Errors.Count == 0,
            plugin_id = PluginId,
            status = "ready",
            system = new
            {
                name = TeknoParrotSystemName,
                referenceId = TeknoParrotSystemReferenceId,
                category = "Arcade"
            },
            paths = new
            {
                root = scan.RootPath,
                executable = scan.ExecutablePath,
                userProfiles = scan.UserProfilesPath,
                gameProfiles = scan.GameProfilesPath,
                gamesRoot = scan.GamesRootPath,
                icons = scan.IconsPath
            },
            profiles_count = scan.Games.Count,
            profile_health = BuildProfileHealth(scan),
            warnings = scan.Warnings,
            errors = scan.Errors
        });
    }

    private static async Task<object> SyncGames(JsonElement data)
    {
        settings = MergeSettings(settings, data);
        var dryRun = GetBool(data, "dryRun") || GetBool(data, "dry_run");
        var scan = TeknoParrotProfileScanner.Scan(settings);
        var payload = BuildTeknoParrotImportPayload(scan.Games, settings, scan);

        if (scan.Errors.Count > 0)
        {
            return new
            {
                success = false,
                dry_run = true,
                games_found = scan.Games.Count,
                errors = scan.Errors,
                warnings = scan.Warnings,
                payload
            };
        }

        if (dryRun || !useSocketIO || socketClient?.IsAuthenticated != true)
        {
            return new
            {
                success = true,
                dry_run = true,
                games_found = scan.Games.Count,
                warnings = scan.Warnings,
                payload
            };
        }

        var result = await HyperImportSyncClient.SendAsync(
            socketClient,
            useSocketIO,
            "teknoparrot:sync_games",
            payload,
            LogAsync,
            LogWarningAsync,
            LogErrorAsync);

        return new
        {
            success = result.Success,
            dry_run = false,
            games_synced = payload.DatabaseOperations.AddGames?.Games.Count ?? 0,
            completed_operations = result.CompletedOperations,
            failed_operations = result.FailedOperations,
            warnings = scan.Warnings
        };
    }

    internal static HyperImportSyncPayload<TeknoParrotProfileGame> BuildTeknoParrotImportPayload(
        IEnumerable<TeknoParrotProfileGame> games,
        TeknoParrotSettings payloadSettings,
        TeknoParrotScanResult? scan = null)
    {
        var gameList = games.OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var executable = FirstNonEmpty(scan?.ExecutablePath, payloadSettings.ExecutablePath, "TeknoParrotUi.exe");
        var userProfilesPath = FirstNonEmpty(scan?.UserProfilesPath, payloadSettings.UserProfilesPath, string.Empty);
        var downloadMedia = payloadSettings.DownloadMedia;

        return new HyperImportSyncPayload<TeknoParrotProfileGame>
        {
            Games = gameList,
            DatabaseOperations = new HyperImportDatabaseOperations
            {
                CreateSystem = new HyperImportSystem
                {
                    Name = TeknoParrotSystemName,
                    DisplayName = TeknoParrotSystemName,
                    ReferenceId = TeknoParrotSystemReferenceId,
                    Description = TeknoParrotSystemDescription,
                    Platform = "Arcade",
                    RomsPaths = userProfilesPath,
                    AllowedExtensions = TeknoParrotAllowedExtensions,
                    SearchSubfolders = false,
                    GamesCount = gameList.Count,
                    MediaOptions = BuildTeknoParrotMediaOptions(downloadMedia),
                    MediaFolders = BuildTeknoParrotMediaFolders(downloadMedia),
                    Metadata = new
                    {
                        source = "teknoparrot_tools_plugin",
                        hyperhqSystemId = TeknoParrotSystemReferenceId,
                        category = "Arcade",
                        developer = "TeknoGods",
                        manufacturer = "Various",
                        media = "HDD",
                        maxControllers = "4",
                        emulated = true
                    }
                },
                CreateEmulator = new HyperImportEmulator
                {
                    Name = "TeknoParrot",
                    DisplayName = "TeknoParrot",
                    CommandLine = TeknoParrotLaunchCommand,
                    Command = TeknoParrotLaunchCommand,
                    Description = "TeknoParrot UI direct profile launcher using UserProfiles XML files as ROM targets",
                    Executable = executable,
                    Platform = "Arcade",
                    Type = "Arcade",
                    SystemName = TeknoParrotSystemName,
                    SupportedExtensions = new[] { ".xml" },
                    LaunchMethod = "command_line",
                    LinkEmulator = new
                    {
                        command = TeknoParrotLaunchCommand,
                        description = "Launch TeknoParrot profiles through TeknoParrotUi.exe"
                    },
                    Metadata = new
                    {
                        source = "teknoparrot_tools_plugin",
                        launcher = "TeknoParrot",
                        requires_client = true,
                        profileFolder = userProfilesPath
                    }
                },
                AddGames = new HyperImportGamesBatch
                {
                    SystemName = TeknoParrotSystemName,
                    Games = gameList.Select(BuildHyperImportGame).ToList()
                }
            },
            SyncMetadata = new HyperImportSyncMetadata
            {
                PluginId = PluginId,
                SyncTime = DateTime.UtcNow,
                GamesCount = gameList.Count
            }
        };
    }

    internal static HyperImportGame BuildHyperImportGame(TeknoParrotProfileGame game)
    {
        var title = FirstNonEmpty(game.Title, game.ProfileName, "TeknoParrot Game");
        var profileName = FirstNonEmpty(game.ProfileName, NormalizeImportId(title));
        var id = $"teknoparrot-{NormalizeImportId(profileName)}";
        var installPath = !string.IsNullOrWhiteSpace(game.GamePath)
            ? Path.GetDirectoryName(game.GamePath) ?? string.Empty
            : string.Empty;

        return new HyperImportGame
        {
            Id = id,
            Title = title,
            Name = title,
            FileName = Path.GetFileName(game.ProfilePath),
            RomPath = game.ProfilePath,
            GameReferenceId = profileName,
            Description = $"TeknoParrot user profile for {title}.",
            Developer = string.Empty,
            Publisher = string.Empty,
            DisplayName = title,
            IsInstalled = !string.IsNullOrWhiteSpace(game.GamePath) && File.Exists(game.GamePath),
            InstallPath = installPath,
            Platform = "Arcade",
            Source = TeknoParrotSystemName,
            LaunchCommandType = 0,
            LaunchCommandFilePath = string.Empty,
            LaunchCommandExtraParams = game.ExtraParameters,
            TitleId = profileName,
            Metadata = new
            {
                source = "teknoparrot_tools_plugin",
                profileName,
                profilePath = game.ProfilePath,
                gamePath = game.GamePath,
                gamePath2 = game.GamePath2,
                executableName = game.ExecutableName,
                executableName2 = game.ExecutableName2,
                hasTwoExecutables = game.HasTwoExecutables,
                iconPath = game.IconPath,
                extraParameters = game.ExtraParameters,
                testMenuParameter = game.TestMenuParameter,
                testMenuExtraParameters = game.TestMenuExtraParameters,
                hyperhqMetadataSystemId = TeknoParrotSystemReferenceId,
                hyperhqSearchName = title,
                warnings = game.Warnings
            }
        };
    }

    internal static object BuildTeknoParrotMediaOptions(bool enabled)
    {
        return new
        {
            themes = false,
            boxart = enabled,
            wheels = enabled,
            banners = enabled,
            carts = false,
            boxbacks = false,
            pointers = false,
            bgImages = enabled,
            marquees = enabled,
            bezels = false,
            videos = false
        };
    }

    internal static object BuildTeknoParrotMediaFolders(bool enabled)
    {
        return new
        {
            downloadBoxes2D = enabled,
            downloadBackgroundsGame = enabled,
            downloadLogosGame = enabled,
            downloadGameMedias2D = false,
            downloadGameThemes = false,
            downloadMarqueesGame = enabled,
            downloadVideoSnaps = false
        };
    }

    private static Task<object> OnboardingStepExecute(JsonElement data)
    {
        var stepId = GetString(data, "stepId") ?? GetString(data, "step_id") ?? "welcome";
        var stepData = data.TryGetProperty("data", out var dataElement) ? dataElement : data;
        settings = MergeSettings(settings, stepData);

        return stepId switch
        {
            "welcome" => Task.FromResult<object>(new { success = true, nextStepId = "paths" }),
            "paths" => GetStatus(),
            "import_options" => GetStatus(),
            "register_games" => Task.FromResult(RegisterGamesForWizard(stepData)),
            "scan_profiles" => Task.FromResult<object>(BuildScanResponse(TeknoParrotProfileScanner.Scan(settings))),
            "preview_sync" => SyncGames(SetDryRun(stepData)),
            "sync_games" => SyncGames(stepData),
            "backup_profiles" => Task.FromResult<object>(BackupProfiles(settings)),
            "finish" => GetStatus(),
            _ => Task.FromResult<object>(new { success = false, error = $"Unknown onboarding step: {stepId}" })
        };
    }

    private static Task<object> Shutdown()
    {
        socketClient?.Dispose();
        return Task.FromResult<object>(new { status = "shutdown" });
    }

    private static object BuildScanResponse(TeknoParrotScanResult scan)
    {
        return new
        {
            success = scan.Errors.Count == 0,
            system = TeknoParrotSystemName,
            system_reference_id = TeknoParrotSystemReferenceId,
            root_path = scan.RootPath,
            executable_path = scan.ExecutablePath,
            user_profiles_path = scan.UserProfilesPath,
            game_profiles_path = scan.GameProfilesPath,
            games_root_path = scan.GamesRootPath,
            icons_path = scan.IconsPath,
            profiles_count = scan.Games.Count,
            games = scan.Games,
            profile_health = BuildProfileHealth(scan),
            warnings = scan.Warnings,
            errors = scan.Errors
        };
    }

    private static object BuildProfileHealth(TeknoParrotScanResult scan)
    {
        var valid = scan.Games
            .Where(game => !string.IsNullOrWhiteSpace(game.GamePath) && File.Exists(game.GamePath))
            .Select(game => game.ProfileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var empty = scan.Games
            .Where(game => string.IsNullOrWhiteSpace(game.GamePath))
            .Select(game => game.ProfileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var broken = scan.Games
            .Where(game => !string.IsNullOrWhiteSpace(game.GamePath) && !File.Exists(game.GamePath))
            .Select(game => game.ProfileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            registered_profiles = scan.Games.Count,
            valid_game_paths = valid.Length,
            broken_game_paths = broken.Length,
            empty_game_paths = empty.Length,
            valid,
            broken,
            empty
        };
    }

    internal static object BackupProfiles(TeknoParrotSettings backupSettings)
    {
        var scan = TeknoParrotProfileScanner.Scan(backupSettings);
        if (scan.Errors.Count > 0)
        {
            return new { success = false, errors = scan.Errors };
        }

        var backupRoot = FirstNonEmpty(
            backupSettings.BackupPath,
            !string.IsNullOrWhiteSpace(scan.RootPath) ? Path.Combine(scan.RootPath, "Backups", "HyperHQ") : string.Empty,
            Path.Combine(AppContext.BaseDirectory, "Backups", "HyperHQ"));

        var backupPath = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backupPath);

        var copied = 0;
        foreach (var file in Directory.GetFiles(scan.UserProfilesPath!, "*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(backupPath, Path.GetFileName(file)), overwrite: false);
            copied++;
        }

        return new { success = true, backup_path = backupPath, profiles_backed_up = copied };
    }

    private static ProfileBackupResult TryBackupProfilesForMutation(TeknoParrotSettings backupSettings)
    {
        var scan = TeknoParrotProfileScanner.Scan(backupSettings);
        if (string.IsNullOrWhiteSpace(scan.UserProfilesPath) || !Directory.Exists(scan.UserProfilesPath))
        {
            return new ProfileBackupResult(true, null, null);
        }

        var profileFiles = Directory.GetFiles(scan.UserProfilesPath, "*.xml", SearchOption.TopDirectoryOnly);
        if (profileFiles.Length == 0)
        {
            return new ProfileBackupResult(true, null, null);
        }

        var response = BackupProfiles(backupSettings);
        var responseElement = JsonSerializer.SerializeToElement(response, JsonOptions);
        var success = responseElement.TryGetProperty("success", out var successElement) &&
            successElement.ValueKind == JsonValueKind.True;
        if (!success)
        {
            var error = responseElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : "Profile backup failed before mutation.";
            if (responseElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array)
            {
                error = string.Join("; ", errorsElement.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString()));
            }

            return new ProfileBackupResult(false, string.IsNullOrWhiteSpace(error) ? "Profile backup failed before mutation." : error, null);
        }

        var backupPath = responseElement.TryGetProperty("backup_path", out var pathElement) &&
            pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()
                : null;
        return new ProfileBackupResult(true, null, backupPath);
    }

    private static object RestoreBackup(TeknoParrotSettings restoreSettings, JsonElement data)
    {
        var backupPath = GetString(data, "backupPath") ?? GetString(data, "backup_path");
        if (string.IsNullOrWhiteSpace(backupPath) || !Directory.Exists(backupPath))
        {
            return new { success = false, error = "A valid backupPath is required." };
        }

        var scan = TeknoParrotProfileScanner.Scan(restoreSettings);
        if (string.IsNullOrWhiteSpace(scan.UserProfilesPath))
        {
            return new { success = false, error = "UserProfiles path is not configured." };
        }

        Directory.CreateDirectory(scan.UserProfilesPath);

        var preRestoreBackupPath = string.Empty;
        var currentProfiles = Directory.GetFiles(scan.UserProfilesPath, "*.xml", SearchOption.TopDirectoryOnly);
        if (currentProfiles.Length > 0)
        {
            var backupRoot = FirstNonEmpty(
                restoreSettings.BackupPath,
                !string.IsNullOrWhiteSpace(scan.RootPath) ? Path.Combine(scan.RootPath, "Backups", "HyperHQ") : string.Empty,
                Path.Combine(AppContext.BaseDirectory, "Backups", "HyperHQ"));

            preRestoreBackupPath = Path.Combine(backupRoot, "pre-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(preRestoreBackupPath);

            foreach (var file in currentProfiles)
            {
                File.Copy(file, Path.Combine(preRestoreBackupPath, Path.GetFileName(file)), overwrite: false);
            }
        }

        var restored = 0;
        foreach (var file in Directory.GetFiles(backupPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(scan.UserProfilesPath, Path.GetFileName(file)), overwrite: true);
            restored++;
        }

        return new
        {
            success = true,
            restored_profiles = restored,
            user_profiles_path = scan.UserProfilesPath,
            pre_restore_backup_path = preRestoreBackupPath
        };
    }

    private static TeknoParrotSettings MergeSettings(TeknoParrotSettings current, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return current;
        }

        TeknoParrotSettings? parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize<TeknoParrotSettings>(data.GetRawText(), JsonOptions);
        }
        catch
        {
            return current;
        }

        var merged = current.MergeWith(parsed ?? new TeknoParrotSettings());
        if (!HasProperty(data, nameof(TeknoParrotSettings.DownloadMedia)))
        {
            merged.DownloadMedia = current.DownloadMedia;
        }

        if (!HasProperty(data, nameof(TeknoParrotSettings.AutoSyncOnDbConnect)))
        {
            merged.AutoSyncOnDbConnect = current.AutoSyncOnDbConnect;
        }

        return merged;
    }

    private static JsonElement SetDryRun(JsonElement data)
    {
        var map = data.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(data.GetRawText(), JsonOptions) ?? new Dictionary<string, object?>()
            : new Dictionary<string, object?>();
        map["dryRun"] = true;
        return JsonSerializer.SerializeToElement(map, JsonOptions);
    }

    internal static string NormalizeImportId(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return normalized.Trim('-');
    }

    internal static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string? GetString(JsonElement data, string propertyName)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty(propertyName, out var value) &&
            value.ValueKind != JsonValueKind.Null &&
            value.ValueKind != JsonValueKind.Undefined)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool GetBool(JsonElement data, string propertyName)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static bool HasProperty(JsonElement data, string propertyName)
    {
        return data.ValueKind == JsonValueKind.Object &&
            data.EnumerateObject().Any(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GenerateAuthToken()
    {
        return Environment.GetEnvironmentVariable("HYPERHQ_AUTH_CHALLENGE") ??
            Environment.GetEnvironmentVariable("HYPERAI_PLUGIN_CHALLENGE") ??
            $"standalone-{Guid.NewGuid():N}";
    }

    private static Task LogAsync(string message)
    {
        Console.Error.WriteLine($"[{PluginId}] INFO: {message}");
        return Task.CompletedTask;
    }

    private static Task LogWarningAsync(string message)
    {
        Console.Error.WriteLine($"[{PluginId}] WARN: {message}");
        return Task.CompletedTask;
    }

    private static Task LogErrorAsync(string message)
    {
        Console.Error.WriteLine($"[{PluginId}] ERROR: {message}");
        return Task.CompletedTask;
    }
}

internal sealed class PluginMessage
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

internal sealed record ProfileBackupResult(bool Success, string? Error, string? BackupPath);

internal sealed record TeknoParrotProfileTemplate(string Code, string TemplatePath, string ExecutableName);

public sealed record TeknoParrotRegistrationItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("game_path")] string GamePath,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("match_type")] string MatchType);

public sealed record TeknoParrotRegistrationIssue(
    [property: JsonPropertyName("exe")] string Exe,
    [property: JsonPropertyName("codes")] string[] Codes,
    [property: JsonPropertyName("best_guess")] string? BestGuess,
    [property: JsonPropertyName("best_score")] double BestScore,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record TeknoParrotRegistrationResult(
    [property: JsonPropertyName("success")]
    bool Success,
    [property: JsonPropertyName("dry_run")]
    bool DryRun,
    [property: JsonPropertyName("errors")]
    IReadOnlyList<string> Errors,
    [property: JsonPropertyName("registered")]
    IReadOnlyList<TeknoParrotRegistrationItem> Registered,
    [property: JsonPropertyName("already_registered")]
    IReadOnlyList<string> AlreadyRegistered,
    [property: JsonPropertyName("ambiguous")]
    IReadOnlyList<TeknoParrotRegistrationIssue> Ambiguous,
    [property: JsonPropertyName("unmatched")]
    IReadOnlyList<string> Unmatched,
    [property: JsonPropertyName("backup_path")]
    string? BackupPath);

public sealed record TeknoParrotRepairItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exe")] string? Exe,
    [property: JsonPropertyName("new_path")] string? NewPath);

public sealed record TeknoParrotRepairResult(
    [property: JsonPropertyName("success")]
    bool Success,
    [property: JsonPropertyName("dry_run")]
    bool DryRun,
    [property: JsonPropertyName("errors")]
    IReadOnlyList<string> Errors,
    [property: JsonPropertyName("repairs")]
    IReadOnlyList<TeknoParrotRepairItem> Repairs,
    [property: JsonPropertyName("backup_path")]
    string? BackupPath);

public sealed class TeknoParrotSettings
{
    public string TeknoParrotRootPath { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string UserProfilesPath { get; set; } = string.Empty;
    public string GameProfilesPath { get; set; } = string.Empty;
    public string GamesRootPath { get; set; } = string.Empty;
    public string IconsPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public bool DownloadMedia { get; set; } = true;
    public bool AutoSyncOnDbConnect { get; set; } = false;

    public TeknoParrotSettings MergeWith(TeknoParrotSettings other)
    {
        return new TeknoParrotSettings
        {
            TeknoParrotRootPath = TeknoParrotToolsPluginMain.FirstNonEmpty(other.TeknoParrotRootPath, TeknoParrotRootPath),
            ExecutablePath = TeknoParrotToolsPluginMain.FirstNonEmpty(other.ExecutablePath, ExecutablePath),
            UserProfilesPath = TeknoParrotToolsPluginMain.FirstNonEmpty(other.UserProfilesPath, UserProfilesPath),
            GameProfilesPath = TeknoParrotToolsPluginMain.FirstNonEmpty(other.GameProfilesPath, GameProfilesPath),
            GamesRootPath = TeknoParrotToolsPluginMain.FirstNonEmpty(other.GamesRootPath, GamesRootPath),
            IconsPath = TeknoParrotToolsPluginMain.FirstNonEmpty(other.IconsPath, IconsPath),
            BackupPath = TeknoParrotToolsPluginMain.FirstNonEmpty(other.BackupPath, BackupPath),
            DownloadMedia = other.DownloadMedia,
            AutoSyncOnDbConnect = other.AutoSyncOnDbConnect
        };
    }
}

public sealed class TeknoParrotProfileGame
{
    public string ProfileName { get; init; } = string.Empty;
    public string ProfilePath { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string GamePath { get; init; } = string.Empty;
    public string GamePath2 { get; init; } = string.Empty;
    public string ExecutableName { get; init; } = string.Empty;
    public string ExecutableName2 { get; init; } = string.Empty;
    public bool HasTwoExecutables { get; init; }
    public string IconPath { get; init; } = string.Empty;
    public string ExtraParameters { get; init; } = string.Empty;
    public string TestMenuParameter { get; init; } = string.Empty;
    public string TestMenuExtraParameters { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = new();
}

public sealed class TeknoParrotScanResult
{
    public string RootPath { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string UserProfilesPath { get; init; } = string.Empty;
    public string GameProfilesPath { get; init; } = string.Empty;
    public string GamesRootPath { get; init; } = string.Empty;
    public string IconsPath { get; init; } = string.Empty;
    public List<TeknoParrotProfileGame> Games { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

public static class TeknoParrotProfileScanner
{
    private const double FuzzyAutoThreshold = 0.72;
    private static readonly string[] GameFileExtensions = { ".exe", ".elf", ".iso", ".gcm", ".gcz", ".bin", ".e4", ".zip", ".xbe", ".dll" };

    public static TeknoParrotScanResult Scan(TeknoParrotSettings settings)
    {
        var rootPath = ResolveRootPath(settings);
        var executablePath = ResolvePath(settings.ExecutablePath, rootPath, "TeknoParrotUi.exe");
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolvePath(settings.GameProfilesPath, rootPath, "GameProfiles");
        var gamesRootPath = ResolveGamesRootPath(settings);
        var iconsPath = ResolvePath(settings.IconsPath, rootPath, "Icons");

        var warnings = new List<string>();
        var errors = new List<string>();
        var games = new List<TeknoParrotProfileGame>();

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            warnings.Add("TeknoParrot root path is not configured.");
        }
        else if (!Directory.Exists(rootPath))
        {
            warnings.Add($"TeknoParrot root path was not found: {rootPath}");
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            warnings.Add("TeknoParrotUi.exe was not found. Set executablePath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            errors.Add("UserProfiles folder was not found. Set userProfilesPath or teknoparrotRootPath.");
            return new TeknoParrotScanResult
            {
                RootPath = rootPath,
                ExecutablePath = executablePath,
                UserProfilesPath = userProfilesPath,
                GameProfilesPath = gameProfilesPath,
                GamesRootPath = gamesRootPath,
                IconsPath = iconsPath,
                Warnings = warnings,
                Errors = errors
            };
        }

        foreach (var profilePath in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                games.Add(ParseProfile(profilePath, rootPath, iconsPath));
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not parse {Path.GetFileName(profilePath)}: {ex.Message}");
            }
        }

        foreach (var duplicateTitle in games
            .GroupBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key))
        {
            warnings.Add($"Duplicate TeknoParrot profile title detected: {duplicateTitle}");
        }

        return new TeknoParrotScanResult
        {
            RootPath = rootPath,
            ExecutablePath = executablePath,
            UserProfilesPath = userProfilesPath,
            GameProfilesPath = gameProfilesPath,
            GamesRootPath = gamesRootPath,
            IconsPath = iconsPath,
            Games = games,
            Warnings = warnings,
            Errors = errors
        };
    }

    internal static string ResolveGamesRootPathForSettings(TeknoParrotSettings settings)
    {
        return ResolveGamesRootPath(settings);
    }

    internal static TeknoParrotProfileGame ParseProfile(string profilePath, string rootPath, string iconsPath)
    {
        var document = XDocument.Load(profilePath);
        var profileName = Path.GetFileNameWithoutExtension(profilePath);
        var title = TeknoParrotToolsPluginMain.FirstNonEmpty(
            FirstElementValue(document, "Description"),
            FirstElementValue(document, "GameName"));
        var gamePath = FirstElementValue(document, "GamePath");
        var gamePath2 = FirstElementValue(document, "GamePath2");
        var executableName = FirstElementValue(document, "ExecutableName");
        var executableName2 = FirstElementValue(document, "ExecutableName2");
        var hasTwoExecutables = string.Equals(FirstElementValue(document, "HasTwoExecutables"), "true", StringComparison.OrdinalIgnoreCase);
        var iconName = FirstElementValue(document, "IconName");
        var extraParameters = FirstElementValue(document, "ExtraParameters");
        var testMenuParameter = FirstElementValue(document, "TestMenuParameter");
        var testMenuExtraParameters = FirstElementValue(document, "TestMenuExtraParameters");
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(title))
        {
            title = HumanizeProfileName(profileName);
            warnings.Add("Description and GameName were missing; using the profile filename.");
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            warnings.Add("GamePath is empty.");
        }
        else if (!File.Exists(gamePath))
        {
            warnings.Add($"GamePath does not exist: {gamePath}");
        }

        if (hasTwoExecutables && !string.IsNullOrWhiteSpace(executableName2))
        {
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                warnings.Add("GamePath2 could not be checked because GamePath is empty.");
            }
            else
            {
                var expectedGamePath2 = Path.Combine(Path.GetDirectoryName(gamePath) ?? string.Empty, executableName2.Trim());
                if (string.IsNullOrWhiteSpace(gamePath2) && File.Exists(expectedGamePath2))
                {
                    warnings.Add($"GamePath2 is empty; expected companion executable at {expectedGamePath2}");
                }
                else if (!string.IsNullOrWhiteSpace(gamePath2) && !File.Exists(gamePath2))
                {
                    warnings.Add($"GamePath2 does not exist: {gamePath2}");
                }
            }
        }

        var iconPath = ResolveIconPath(iconName, rootPath, iconsPath);

        return new TeknoParrotProfileGame
        {
            ProfileName = profileName,
            ProfilePath = profilePath,
            Title = title.Trim(),
            GamePath = gamePath.Trim(),
            GamePath2 = gamePath2.Trim(),
            ExecutableName = executableName.Trim(),
            ExecutableName2 = executableName2.Trim(),
            HasTwoExecutables = hasTwoExecutables,
            IconPath = iconPath,
            ExtraParameters = extraParameters.Trim(),
            TestMenuParameter = testMenuParameter.Trim(),
            TestMenuExtraParameters = testMenuExtraParameters.Trim(),
            Warnings = warnings
        };
    }

    internal static TeknoParrotRegistrationResult RegisterGames(TeknoParrotSettings settings, bool dryRun)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolvePath(settings.GameProfilesPath, rootPath, "GameProfiles");
        var gamesRootPath = ResolveGamesRootPath(settings);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(gameProfilesPath) || !Directory.Exists(gameProfilesPath))
        {
            errors.Add("GameProfiles folder was not found. Set gameProfilesPath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(gamesRootPath) || !Directory.Exists(gamesRootPath))
        {
            errors.Add("Games root folder was not found. Set gamesRootPath before registering profiles.");
        }

        if (string.IsNullOrWhiteSpace(userProfilesPath))
        {
            errors.Add("UserProfiles path could not be resolved.");
        }

        if (errors.Count > 0)
        {
            return new TeknoParrotRegistrationResult(false, dryRun, errors, Array.Empty<TeknoParrotRegistrationItem>(), Array.Empty<string>(), Array.Empty<TeknoParrotRegistrationIssue>(), Array.Empty<string>(), null);
        }

        if (!dryRun)
        {
            Directory.CreateDirectory(userProfilesPath);
        }

        var profileIndex = BuildProfileIndex(gameProfilesPath);
        var profileCodes = Directory.GetFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Where(IsSafeProfileCode)
            .ToArray();
        var registered = new List<TeknoParrotRegistrationItem>();
        var already = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new List<TeknoParrotRegistrationIssue>();
        var matchedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in GetGameFiles(gamesRootPath))
        {
            var relative = Path.GetRelativePath(gamesRootPath, file);
            var folderName = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(file);
            var folderKey = StripGameFolderSuffix(folderName);
            allFolders[folderKey] = folderName;
            var fileName = Path.GetFileName(file);
            if (!profileIndex.TryGetValue(fileName, out var templates))
            {
                continue;
            }

            var selected = SelectRegistrationTemplate(folderKey, templates);
            if (selected.Template is null)
            {
                ambiguous.Add(new TeknoParrotRegistrationIssue(file, templates.Select(template => template.Code).ToArray(), selected.BestGuess, selected.Score, "shared-executable"));
                continue;
            }

            var code = selected.Template.Code;
            matchedFolders.Add(folderKey);
            var destination = Path.Combine(userProfilesPath, $"{code}.xml");
            if (File.Exists(destination))
            {
                already.Add(code);
                if (!dryRun)
                {
                    BackfillSecondaryExecutablePath(destination);
                }

                continue;
            }

            if (!IsSafeProfileCode(code))
            {
                ambiguous.Add(new TeknoParrotRegistrationIssue(file, new[] { code }, code, selected.Score, "invalid-profile-code"));
                continue;
            }

            if (!dryRun)
            {
                CopyTemplateWithGamePath(selected.Template.TemplatePath, destination, file);
            }

            registered.Add(new TeknoParrotRegistrationItem(code, file, selected.Score, selected.MatchType));
        }

        foreach (var folder in allFolders.Where(pair => !matchedFolders.Contains(pair.Key)).Select(pair => pair.Value).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var selected = SelectProfileCodeByFolderName(folder, profileCodes);
            if (selected.Code is null)
            {
                continue;
            }

            var destination = Path.Combine(userProfilesPath, $"{selected.Code}.xml");
            if (File.Exists(destination))
            {
                already.Add(selected.Code);
                continue;
            }

            var templatePath = Path.Combine(gameProfilesPath, $"{selected.Code}.xml");
            if (!File.Exists(templatePath))
            {
                continue;
            }

            var folderPath = Path.Combine(gamesRootPath, folder);
            var file = GetGameFiles(folderPath).OrderBy(GetGameFilePriority).FirstOrDefault();
            if (file is null)
            {
                continue;
            }

            if (!dryRun)
            {
                CopyTemplateWithGamePath(templatePath, destination, file);
            }

            registered.Add(new TeknoParrotRegistrationItem(selected.Code, file, selected.Score, "profile-code-fuzzy"));
            matchedFolders.Add(StripGameFolderSuffix(folder));
        }

        var unmatched = allFolders
            .Where(pair => !matchedFolders.Contains(pair.Key))
            .Select(pair => pair.Value)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TeknoParrotRegistrationResult(true, dryRun, Array.Empty<string>(), registered.ToArray(), already.ToArray(), ambiguous.ToArray(), unmatched, null);
    }

    internal static TeknoParrotRepairResult RepairGamePaths(TeknoParrotSettings settings, bool dryRun)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolvePath(settings.GameProfilesPath, rootPath, "GameProfiles");
        var gamesRootPath = ResolveGamesRootPath(settings);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            errors.Add("UserProfiles folder was not found. Set userProfilesPath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(gameProfilesPath) || !Directory.Exists(gameProfilesPath))
        {
            errors.Add("GameProfiles folder was not found. Set gameProfilesPath or teknoparrotRootPath.");
        }

        if (string.IsNullOrWhiteSpace(gamesRootPath) || !Directory.Exists(gamesRootPath))
        {
            errors.Add("Games root folder was not found. Set gamesRootPath before repairing profiles.");
        }

        if (errors.Count > 0)
        {
            return new TeknoParrotRepairResult(false, dryRun, errors, Array.Empty<TeknoParrotRepairItem>(), null);
        }

        var profileIndex = BuildProfileIndex(gameProfilesPath);
        var gameFiles = GetGameFiles(gamesRootPath)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key ?? string.Empty, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var reports = new List<TeknoParrotRepairItem>();

        foreach (var profilePath in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(profilePath);
            }
            catch
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "parse-failed", null, null));
                continue;
            }

            var gamePath = FirstElementValue(document, "GamePath");
            if (!string.IsNullOrWhiteSpace(gamePath) && File.Exists(gamePath))
            {
                continue;
            }

            var executableName = FirstElementValue(document, "ExecutableName");
            if (string.IsNullOrWhiteSpace(executableName))
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "no-executable-name", null, null));
                continue;
            }

            var candidates = new List<string>();
            var ambiguousProfile = false;
            foreach (var alternative in GetExecutableAlternatives(executableName))
            {
                if (profileIndex.TryGetValue(alternative, out var templates) && templates.Count > 1)
                {
                    ambiguousProfile = true;
                    break;
                }

                if (gameFiles.TryGetValue(alternative, out var files))
                {
                    candidates.AddRange(files);
                }
            }

            if (ambiguousProfile || candidates.Count > 1)
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "ambiguous", executableName, null));
                continue;
            }

            if (candidates.Count == 0)
            {
                reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "not-found", executableName, null));
                continue;
            }

            var newPath = candidates[0];
            if (!dryRun)
            {
                SetElementValue(document, "GamePath", newPath);
                SetSecondaryExecutablePath(document, newPath);
                SaveProfileDocument(document, profilePath);
            }

            reports.Add(new TeknoParrotRepairItem(Path.GetFileNameWithoutExtension(profilePath), "fixed", executableName, newPath));
        }

        return new TeknoParrotRepairResult(true, dryRun, Array.Empty<string>(), reports.ToArray(), null);
    }

    private static string ResolveRootPath(TeknoParrotSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TeknoParrotRootPath))
        {
            return Path.GetFullPath(settings.TeknoParrotRootPath);
        }

        if (!string.IsNullOrWhiteSpace(settings.ExecutablePath))
        {
            var executableDirectory = Path.GetDirectoryName(settings.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(executableDirectory))
            {
                return Path.GetFullPath(executableDirectory);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.UserProfilesPath))
        {
            var userProfilesDirectory = Path.GetDirectoryName(settings.UserProfilesPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(userProfilesDirectory))
            {
                return Path.GetFullPath(userProfilesDirectory);
            }
        }

        return string.Empty;
    }

    private static string ResolveGamesRootPath(TeknoParrotSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GamesRootPath))
        {
            return Path.GetFullPath(settings.GamesRootPath);
        }

        var rootPath = ResolveRootPath(settings);
        return string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(rootPath, "Games"));
    }

    private static string ResolvePath(string explicitPath, string rootPath, string childName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        return string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(rootPath, childName));
    }

    private static string ResolveIconPath(string iconName, string rootPath, string iconsPath)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return string.Empty;
        }

        if (Path.IsPathFullyQualified(iconName))
        {
            return iconName;
        }

        var rootCandidate = !string.IsNullOrWhiteSpace(rootPath) ? Path.GetFullPath(Path.Combine(rootPath, iconName)) : string.Empty;
        if (!string.IsNullOrWhiteSpace(rootCandidate) && File.Exists(rootCandidate))
        {
            return rootCandidate;
        }

        return !string.IsNullOrWhiteSpace(iconsPath)
            ? Path.GetFullPath(Path.Combine(iconsPath, Path.GetFileName(iconName)))
            : iconName;
    }

    private static Dictionary<string, List<TeknoParrotProfileTemplate>> BuildProfileIndex(string gameProfilesPath)
    {
        var index = new Dictionary<string, List<TeknoParrotProfileTemplate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var templatePath in Directory.GetFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var executableName = GetPrimaryExecutableName(templatePath);
            if (string.IsNullOrWhiteSpace(executableName))
            {
                continue;
            }

            var code = Path.GetFileNameWithoutExtension(templatePath);
            foreach (var alternative in GetExecutableAlternatives(executableName))
            {
                if (!index.TryGetValue(alternative, out var templates))
                {
                    templates = new List<TeknoParrotProfileTemplate>();
                    index[alternative] = templates;
                }

                templates.Add(new TeknoParrotProfileTemplate(code, templatePath, executableName));
            }
        }

        return index;
    }

    private static string GetPrimaryExecutableName(string templatePath)
    {
        try
        {
            var document = XDocument.Load(templatePath);
            return FirstElementValue(document, "ExecutableName");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> GetExecutableAlternatives(string executableName)
    {
        return executableName
            .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    private static (TeknoParrotProfileTemplate? Template, string? BestGuess, double Score, string MatchType) SelectRegistrationTemplate(
        string folderName,
        List<TeknoParrotProfileTemplate> templates)
    {
        if (templates.Count == 1)
        {
            return (templates[0], templates[0].Code, 1.0, "executable");
        }

        var selected = SelectProfileCodeByFolderName(folderName, templates.Select(template => template.Code));
        if (selected.Code is null || selected.Score < FuzzyAutoThreshold)
        {
            return (null, selected.Code, selected.Score, "shared-executable");
        }

        return (templates.First(template => string.Equals(template.Code, selected.Code, StringComparison.OrdinalIgnoreCase)), selected.Code, selected.Score, "fuzzy");
    }

    private static (string? Code, double Score) SelectProfileCodeByFolderName(string folderName, IEnumerable<string> profileCodes)
    {
        var normalizedFolder = NormalizeGameKey(StripGameFolderSuffix(folderName));
        if (normalizedFolder.Length < 2)
        {
            return (null, 0);
        }

        string? bestCode = null;
        var bestScore = 0.0;
        foreach (var code in profileCodes)
        {
            var score = GetDiceSimilarity(normalizedFolder, NormalizeGameKey(code));
            if (score > bestScore)
            {
                bestScore = score;
                bestCode = code;
            }
        }

        return bestScore >= FuzzyAutoThreshold ? (bestCode, bestScore) : (null, bestScore);
    }

    private static IEnumerable<string> GetGameFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        var baseDepth = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Length;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                if (GameFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(extension))
                {
                    return false;
                }

                var depth = Path.GetFullPath(path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Length - baseDepth;
                return depth <= 6;
            });
    }

    private static int GetGameFilePriority(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".exe" => 0,
            ".elf" => 1,
            "" => 2,
            ".xbe" => 3,
            ".dll" => 4,
            _ => 5
        };
    }

    private static void CopyTemplateWithGamePath(string templatePath, string destinationPath, string gamePath)
    {
        var document = XDocument.Load(templatePath);
        SetElementValue(document, "GamePath", gamePath);
        SetSecondaryExecutablePath(document, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        SaveProfileDocument(document, destinationPath);
    }

    private static void BackfillSecondaryExecutablePath(string userProfilePath)
    {
        try
        {
            var document = XDocument.Load(userProfilePath);
            var gamePath = FirstElementValue(document, "GamePath");
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                return;
            }

            if (SetSecondaryExecutablePath(document, gamePath))
            {
                SaveProfileDocument(document, userProfilePath);
            }
        }
        catch
        {
            // Existing malformed profiles are reported by Scan; registration should keep moving.
        }
    }

    private static bool SetSecondaryExecutablePath(XDocument document, string primaryGamePath)
    {
        var hasTwoExecutables = string.Equals(FirstElementValue(document, "HasTwoExecutables"), "true", StringComparison.OrdinalIgnoreCase);
        var executableName2 = FirstElementValue(document, "ExecutableName2");
        if (!hasTwoExecutables || string.IsNullOrWhiteSpace(executableName2) || string.IsNullOrWhiteSpace(primaryGamePath))
        {
            return false;
        }

        var secondaryPath = Path.Combine(Path.GetDirectoryName(primaryGamePath) ?? string.Empty, executableName2.Trim());
        if (!File.Exists(secondaryPath))
        {
            return false;
        }

        if (string.Equals(FirstElementValue(document, "GamePath2"), secondaryPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SetElementValue(document, "GamePath2", secondaryPath);
        return true;
    }

    private static void SetElementValue(XDocument document, string localName, string value)
    {
        var root = document.Root ?? throw new InvalidOperationException("Profile XML has no root element.");
        var existing = root.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new XElement(root.GetDefaultNamespace() + localName);
            root.AddFirst(existing);
        }

        existing.Value = value;
    }

    private static void SaveProfileDocument(XDocument document, string profilePath)
    {
        var tempPath = profilePath + ".tmp";
        document.Save(tempPath);
        if (!File.Exists(profilePath))
        {
            File.Move(tempPath, profilePath);
            return;
        }

        try
        {
            File.Replace(tempPath, profilePath, null);
        }
        catch
        {
            File.Delete(profilePath);
            File.Move(tempPath, profilePath);
        }
    }

    private static string StripGameFolderSuffix(string folderName)
    {
        return Regex.Replace(folderName, @"\.(teknoparrot|parrot|game)$", string.Empty, RegexOptions.IgnoreCase);
    }

    private static bool IsSafeProfileCode(string code)
    {
        return Regex.IsMatch(code, @"^[\w]+$");
    }

    private static string NormalizeGameKey(string value)
    {
        var normalized = Regex.Replace(value, "(?<=[a-z])(?=[A-Z])", " ");
        normalized = Regex.Replace(normalized, "(?<=[A-Z])(?=[A-Z][a-z])", " ");
        normalized = Regex.Replace(normalized, @"\(\d{4}-\d{2}-\d{2}\)", string.Empty);
        normalized = Regex.Replace(normalized, @"\(\d{4}\)", string.Empty);
        normalized = Regex.Replace(normalized, @"\[[^\]]*\]", string.Empty);
        normalized = Regex.Replace(normalized, @"\(\d+\.\d[\d\.]*\)", string.Empty);
        normalized = Regex.Replace(normalized, @"\((JPN|USA|EUR|EXP|JP|US|KOR|AUS|ASI|INTL|ARC|UNK)\)", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\((ver\.?|rev\.?|v)\s*[\d\.]+[a-z]?\)", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\(\d+\)", string.Empty);
        normalized = Regex.Replace(normalized, @"[^\p{L}0-9]", string.Empty);
        return normalized.ToLowerInvariant();
    }

    private static double GetDiceSimilarity(string first, string second)
    {
        if (first.Length < 2 || second.Length < 2)
        {
            return 0;
        }

        var firstBigrams = BuildBigrams(first);
        var secondBigrams = BuildBigrams(second);
        var intersection = 0;
        foreach (var pair in firstBigrams)
        {
            if (secondBigrams.TryGetValue(pair.Key, out var count))
            {
                intersection += Math.Min(pair.Value, count);
            }
        }

        var total = firstBigrams.Values.Sum() + secondBigrams.Values.Sum();
        return total == 0 ? 0 : (2.0 * intersection) / total;
    }

    private static Dictionary<string, int> BuildBigrams(string value)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < value.Length - 1; index++)
        {
            var bigram = value.Substring(index, 2);
            result[bigram] = result.GetValueOrDefault(bigram) + 1;
        }

        return result;
    }

    private static string FirstElementValue(XDocument document, string localName)
    {
        return document
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim() ?? string.Empty;
    }

    private static string HumanizeProfileName(string profileName)
    {
        var chars = new List<char>();
        for (var index = 0; index < profileName.Length; index++)
        {
            var current = profileName[index];
            if (index > 0 && char.IsUpper(current) && char.IsLower(profileName[index - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(current);
        }

        return new string(chars.ToArray()).Replace('_', ' ').Replace('-', ' ').Trim();
    }
}
