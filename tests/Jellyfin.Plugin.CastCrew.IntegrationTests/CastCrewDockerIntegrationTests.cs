using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Jellyfin.Plugin.CastCrew.IntegrationTests;

/// <summary>
/// Custom Fact attribute that auto-skips when Docker is not available.
/// Tests using this attribute run automatically when Docker is detected —
/// no environment variable opt-in is required.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!IsDockerAvailable())
        {
            Skip = "Docker is not available on this machine. Install Docker to run container-based integration tests.";
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "info",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Shared fixture that manages a fresh Jellyfin Docker container with the CastCrew plugin.
/// <para>
/// Lifecycle guarantees:
/// - A brand-new container is created for each test run (no leftover state).
/// - On dispose, the container is stopped and removed.
/// - The Testcontainers Ryuk sidecar ensures cleanup even on test process crash.
/// </para>
/// </summary>
public sealed class JellyfinDockerFixture : IAsyncLifetime
{
    private const string JellyfinImage = "jellyfin/jellyfin:12.0";
    private const int JellyfinPort = 8096;
    private static readonly TimeSpan ContainerStartupTimeout = TimeSpan.FromMinutes(2);

    private IContainer? _container;
    private string? _tempPluginDir;

    public string BaseUrl { get; private set; } = string.Empty;
    public string? AccessToken { get; private set; }
    public bool IsReady { get; private set; }
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        var pluginDllPath = ResolvePluginDllPath();
        if (pluginDllPath is null)
        {
            SkipReason = "Plugin DLL not found in build output. Build the plugin project first.";
            return;
        }

        try
        {
            // Prepare a temp directory with the plugin DLL in the expected structure.
            // Using a bind mount ensures the DLL ends up at the exact path Jellyfin expects.
            var tempPluginDir = Path.Combine(Path.GetTempPath(), "castcrew-docker-test-" + Guid.NewGuid().ToString("N"));
            _tempPluginDir = tempPluginDir;
            var pluginVersionDir = Path.Combine(tempPluginDir, "plugins", "CastCrew");
            Directory.CreateDirectory(pluginVersionDir);
            File.Copy(pluginDllPath, Path.Combine(pluginVersionDir, "Jellyfin.Plugin.CastCrew.dll"));

            _container = new ContainerBuilder(JellyfinImage)
                .WithEnvironment("PUID", "1000")
                .WithEnvironment("PGID", "1000")
                .WithEnvironment("TZ", "UTC")
                .WithPortBinding(JellyfinPort, true)
                .WithAutoRemove(true)
                .WithCleanUp(true)
                .WithBindMount(tempPluginDir, "/config")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request =>
                        request.ForPath("/Startup/Configuration").ForPort((ushort)JellyfinPort)))
                .Build();

            using var startCts = new CancellationTokenSource(ContainerStartupTimeout);
            await _container.StartAsync(startCts.Token);

            var mappedPort = _container.GetMappedPublicPort(JellyfinPort);
            BaseUrl = $"http://localhost:{mappedPort}/";

            // The setup host answers /System/Info/Public and degraded /health with
            // HTTP 200 during migrations. Wait for an actual wizard API instead.
            await CompleteSetupWizardAsync();
            IsReady = true;
        }
        catch (OperationCanceledException)
        {
            SkipReason = "Jellyfin container did not become ready within the timeout period.";
        }
        catch (Exception ex)
        {
            SkipReason = $"Failed to start Jellyfin container: {ex}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            // Stop and remove the container — ensures no leftover Jellyfin instance
            await _container.DisposeAsync();
            _container = null;
        }

        // Clean up the temporary plugin directory
        if (_tempPluginDir is not null && Directory.Exists(_tempPluginDir))
        {
            try
            {
                Directory.Delete(_tempPluginDir, recursive: true);
            }
            catch
            {
                // Best effort — temp dir cleanup is not critical
            }

            _tempPluginDir = null;
        }
    }

    public HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            "MediaBrowser Client=\"CastCrewDockerTests\", Device=\"CLI\", DeviceId=\"docker-test\", Version=\"1.0\"");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(AccessToken))
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
                $"MediaBrowser Client=\"CastCrewDockerTests\", Device=\"CLI\", DeviceId=\"docker-test\", Version=\"1.0\", Token=\"{AccessToken}\"");
        }

        return client;
    }

    private async Task CompleteSetupWizardAsync()
    {
        using var client = CreateHttpClient();

        // Check if the server is freshly started (wizard not complete)
        var infoResponse = await client.GetAsync("System/Info/Public");
        var infoJson = await infoResponse.Content.ReadAsStringAsync();

        // If wizard is already complete, just authenticate
        if (infoJson.Contains("\"StartupWizardCompleted\":true", StringComparison.OrdinalIgnoreCase))
        {
            await AuthenticateAsync(client);
            return;
        }

        // Run through the startup wizard steps
        var configResponse = await client.PostAsJsonAsync("Startup/Configuration", new
        {
            UICulture = "en-US",
            MetadataCountryCode = "US",
            PreferredMetadataLanguage = "en"
        });
        configResponse.EnsureSuccessStatusCode();

        // GET the default user first (required by some Jellyfin versions)
        await client.GetAsync("Startup/User");

        var userResponse = await client.PostAsJsonAsync("Startup/User", new
        {
            Name = "admin",
            Password = "admin123"
        });
        userResponse.EnsureSuccessStatusCode();

        var completeResponse = await client.PostAsJsonAsync("Startup/Complete", new { });
        completeResponse.EnsureSuccessStatusCode();

        // Small delay to allow server to finalize wizard completion
        await Task.Delay(1000);

        await AuthenticateAsync(client);
    }

    private async Task AuthenticateAsync(HttpClient client)
    {
        // Retry auth a few times in case the server needs a moment post-wizard
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var loginResponse = await client.PostAsJsonAsync("Users/AuthenticateByName", new
            {
                Username = "admin",
                Pw = "admin123"
            });

            if (loginResponse.IsSuccessStatusCode)
            {
                var auth = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
                AccessToken = auth?.AccessToken;
                return;
            }

            await Task.Delay(2000);
        }
    }

    private static string? ResolvePluginDllPath()
        => typeof(CastCrewPlugin).Assembly.Location;

    private sealed class AuthResponse
    {
        [JsonPropertyName("AccessToken")]
        public string AccessToken { get; set; } = string.Empty;
    }
}

[CollectionDefinition("Docker")]
public class DockerCollection : ICollectionFixture<JellyfinDockerFixture>
{
}

/// <summary>
/// Integration tests that verify CastCrew plugin behavior inside a Docker-based
/// Jellyfin instance where the web root (/usr/share/jellyfin/web/) is read-only.
/// <para>
/// These tests auto-skip when Docker is not available on the host machine.
/// A completely fresh Jellyfin container is created for the test run and
/// destroyed when tests complete.
/// </para>
/// </summary>
[Collection("Docker")]
public class CastCrewDockerIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly JellyfinDockerFixture _fixture;

    public CastCrewDockerIntegrationTests(JellyfinDockerFixture fixture)
    {
        _fixture = fixture;
    }

    private void EnsureReady()
    {
        Assert.True(_fixture.IsReady, _fixture.SkipReason ?? "Docker fixture not ready.");
    }

    [DockerFact]
    public void PluginLoads_InDockerContainer()
    {
        EnsureReady();
        using var client = _fixture.CreateHttpClient();
        var plugins = client.GetStringAsync("Plugins").GetAwaiter().GetResult();
        Assert.Contains("CastCrew", plugins);
        Assert.DoesNotContain("NotSupported", plugins);
        Assert.DoesNotContain("Malfunctioned", plugins);
    }

    [DockerFact]
    public async Task MenuLink_IsInjected_IntoWebConfig()
    {
        EnsureReady();
        using var client = _fixture.CreateHttpClient();
        var response = await client.GetAsync("web/config.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(json.RootElement.GetProperty("menuLinks").EnumerateArray(), link =>
            link.GetProperty("name").GetString() == "Cast&Crew" &&
            link.GetProperty("url").GetString()!.Contains("cast_crew", StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task ConfigPage_IsRegistered_InDockerContainer()
    {
        EnsureReady();

        using var client = _fixture.CreateHttpClient();
        var response = await client.GetAsync("web/configurationpages");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var pages = await response.Content.ReadFromJsonAsync<List<ConfigPageInfo>>(JsonOptions);
        Assert.NotNull(pages);

        var configPage = pages!.FirstOrDefault(p =>
            string.Equals(p.Name, "castcrew-config", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(configPage);
        Assert.Equal("CastCrew", configPage!.DisplayName);
    }

    [DockerFact]
    public async Task ConfigPage_ServesContent()
    {
        EnsureReady();

        using var client = _fixture.CreateHttpClient();
        var response = await client.GetAsync("web/configurationpage?name=castcrew-config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("<!DOCTYPE html>", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CastCrew", content, StringComparison.OrdinalIgnoreCase);
    }

    [DockerFact]
    public async Task ActorsEndpoint_ReturnsValidResponse_InDockerContainer()
    {
        EnsureReady();

        using var client = _fixture.CreateHttpClient();
        var response = await client.GetAsync("CastCrew/Actors?startIndex=0&limit=10&sortBy=Name");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ActorsResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.NotNull(payload!.Items);
        Assert.True(payload.TotalRecordCount >= 0);
        Assert.Equal("Name", payload.SortBy);
    }

    [DockerFact]
    public async Task DirectorsEndpoint_ReturnsValidResponse_InDockerContainer()
    {
        EnsureReady();

        using var client = _fixture.CreateHttpClient();
        var response = await client.GetAsync("CastCrew/Directors?startIndex=0&limit=10&sortBy=Name");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ActorsResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.NotNull(payload!.Items);
        Assert.True(payload.TotalRecordCount >= 0);
    }

    [DockerFact]
    public async Task ProducersEndpoint_ReturnsValidResponse_InDockerContainer()
    {
        EnsureReady();

        using var client = _fixture.CreateHttpClient();
        var response = await client.GetAsync("CastCrew/Producers?startIndex=0&limit=10&sortBy=Name");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ActorsResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.NotNull(payload!.Items);
        Assert.True(payload.TotalRecordCount >= 0);
    }

    [DockerFact]
    public async Task Server_RemainsHealthy_AfterPluginInitialization()
    {
        EnsureReady();

        using var client = _fixture.CreateHttpClient();
        var response = await client.GetAsync("System/Info/Public");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class ConfigPageInfo
    {
        public string Name { get; set; } = string.Empty;
        public bool EnableInMainMenu { get; set; }
        public string? MenuIcon { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string? PluginId { get; set; }
    }

    private sealed class ActorsResponse
    {
        public List<JsonElement> Items { get; set; } = new();
        public int TotalRecordCount { get; set; }
        public int StartIndex { get; set; }
        public int PageSize { get; set; }
        public string SortBy { get; set; } = string.Empty;
        public string DetailRoutePreference { get; set; } = string.Empty;
    }
}
