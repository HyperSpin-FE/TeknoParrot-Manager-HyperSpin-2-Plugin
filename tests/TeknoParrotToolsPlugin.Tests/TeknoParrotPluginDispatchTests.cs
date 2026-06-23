using System.Text.Json;
using TeknoParrotToolsPlugin;
using Xunit;

namespace TeknoParrotToolsPlugin.Tests;

public class TeknoParrotPluginDispatchTests
{
    [Fact]
    public async Task RunSetupWizard_action_returns_manifest_wizard_id()
    {
        var response = await TeknoParrotToolsPluginMain.ProcessMessage(JsonSerializer.Serialize(new
        {
            method = "execute",
            data = new
            {
                action = "run_setup_wizard"
            }
        }));

        var json = JsonSerializer.Serialize(response);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"wizard_id\":\"teknoparrot-tools-setup\"", json);
    }

    [Fact]
    public async Task GetStatus_method_reports_valid_profile_health()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteProfile("abc", "After Burner Climax", fixture.WriteGameExecutable("abc.exe"));

        await TeknoParrotToolsPluginMain.ProcessMessage(JsonSerializer.Serialize(new
        {
            method = "initialize",
            data = new
            {
                teknoparrotRootPath = fixture.RootPath,
                gamesRootPath = Path.Combine(fixture.RootPath, "Games")
            }
        }));

        var response = await TeknoParrotToolsPluginMain.ProcessMessage(JsonSerializer.Serialize(new
        {
            method = "get_status"
        }));

        var json = JsonSerializer.Serialize(response);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"plugin_id\":\"teknoparrot-tools\"", json);
        Assert.Contains("\"profiles_count\":1", json);
        Assert.Contains("\"valid_game_paths\":1", json);
    }

    [Fact]
    public async Task OnboardingRegisterGames_step_skips_when_games_root_is_missing()
    {
        using var fixture = new TeknoParrotFixture();
        var missingGamesRoot = Path.Combine(fixture.RootPath, "MissingGames");

        var response = await TeknoParrotToolsPluginMain.ProcessMessage(JsonSerializer.Serialize(new
        {
            method = "onboarding/step-execute",
            data = new
            {
                stepId = "register_games",
                data = new
                {
                    teknoparrotRootPath = fixture.RootPath,
                    gamesRootPath = missingGamesRoot
                }
            }
        }));

        var json = JsonSerializer.Serialize(response);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"skipped\":true", json);
        Assert.Contains("No games root folder configured", json);
        Assert.Empty(Directory.GetFiles(fixture.UserProfilesPath, "*.xml", SearchOption.TopDirectoryOnly));
    }
}
