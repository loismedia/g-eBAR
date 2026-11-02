using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GeBar.App;
using GeBar.Core;

namespace GeBar.Tests;

public class UpdateChannelTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static object Release(string version, bool nightly, bool executable = true) => new
    {
        tag_name = "v" + version,
        draft = false,
        prerelease = nightly,
        body = "g-eHID: 02.11-hid-ABCDE",
        assets = new[]
        {
            new { name = executable ? "g-eBAR.exe" : "other.exe", url = "https://api.github.com/repos/loismedia/g-eBAR/releases/assets/1", digest = "sha256:" + new string('A', 64) },
            new { name = "g-eHID.exe", url = "https://api.github.com/repos/loismedia/g-eBAR/releases/assets/2", digest = "sha256:" + new string('B', 64) }
        }
    };

    [Fact]
    public async Task PublicIgnoresNightlyAndDoesNotSendStoredDevToken()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Contains("/loismedia/g-eBAR/", request.RequestUri!.AbsolutePath);
                Assert.Null(request.Headers.Authorization);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { Release("99.0.0", true), Release("2.0.0", false), Release("98.0.0", false, false) })) };
            }));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, "expired-token", ReleaseChannel.Public);

            GitHubRelease? release = await service.CheckAsync(CancellationToken.None);

            Assert.NotNull(release);
            Assert.Equal(new Version(2, 0, 0), release.Version);
            Assert.False(release.IsNightly);
            Assert.Null(release.TargetChannel);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData(null)]
    [InlineData("99.0.0")]
    public async Task ExplicitChannelSwitchAcceptsOlderEqualAndNewerVersions(string? version)
    {
        version ??= typeof(AppSettings).Assembly.GetName().Version!.ToString(3);
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Null(request.Headers.Authorization);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { Release(version, false) })) };
            }));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, "expired-token", ReleaseChannel.Dev);

            GitHubRelease? release = await service.CheckAsync(CancellationToken.None, targetChannel: ReleaseChannel.Public);

            Assert.NotNull(release);
            Assert.True(release.AppUpdateAvailable);
            Assert.Equal(ReleaseChannel.Public, release.TargetChannel);
            Assert.True(release.HidUpdateAvailable);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DevSwitchUsesPrivateRepositoryAndToken()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Contains("/loismedia/pg/", request.RequestUri!.AbsolutePath);
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { Release("99.0.0", true) })) };
            }));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, "test-token", ReleaseChannel.Public);

            GitHubRelease? release = await service.CheckAsync(CancellationToken.None, targetChannel: ReleaseChannel.Dev);

            Assert.NotNull(release);
            Assert.Equal(ReleaseChannel.Dev, release.TargetChannel);
            Assert.True(release.IsNightly);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PublicHistoryUsesReleaseBodiesAndFiltersNightly()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Contains("/loismedia/g-eBAR/releases", request.RequestUri!.AbsolutePath);
                Assert.Null(request.Headers.Authorization);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { Release("99.0.0", true), Release("2.0.0", false) })) };
            }));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, "expired-token", ReleaseChannel.Dev);

            string history = await service.DownloadChangelogAsync(null, CancellationToken.None, ReleaseChannel.Public);

            Assert.Contains("g-eBAR v2.0.0", history);
            Assert.DoesNotContain("v99.0.0", history);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DevHistoryKeepsVersionsAfterTheirReleasesAreDeleted()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-{Guid.NewGuid():N}");
        const string archive = "# Changelog\n\n## g-eBAR v1.3.12\n\n- Current\n\n## g-eBAR v1.3.11\n\n- Previous\n\n## g-eBAR v1.3.10\n\n- Archived\n\n## g-eBAR v1.2.0\n\n- Older";
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                Assert.Contains("/loismedia/pg/", request.RequestUri!.AbsolutePath);
                if (request.RequestUri.AbsolutePath.EndsWith("/contents/CHANGELOG.md", StringComparison.Ordinal))
                {
                    Assert.Equal("?ref=v1.3.12", request.RequestUri.Query);
                    Assert.Contains(request.Headers.Accept, header => header.MediaType == "application/vnd.github.raw+json");
                    return new(HttpStatusCode.OK) { Content = new StringContent(archive) };
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[]
                {
                    Release("1.3.11", false), Release("99.0.0", false, false), Release("1.3.12", false)
                })) };
            }));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, "test-token", ReleaseChannel.Dev);

            string history = await service.DownloadChangelogAsync(null, CancellationToken.None);

            Assert.Equal(archive, history);
            Assert.Contains("v1.3.10", history);
            Assert.Contains("v1.2.0", history);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HistoryForDevChannelSwitchUsesTheRequestedTag()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-{Guid.NewGuid():N}");
        const string archive = "# Changelog\n\n## g-eBAR v1.5.0\n\n- Dev update\n\n## g-eBAR v1.3.12\n\n- Bridge";
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("/repos/loismedia/pg/contents/CHANGELOG.md", request.RequestUri!.AbsolutePath);
                Assert.Equal("?ref=v1.5.0", request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = new StringContent(archive) };
            }));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, "test-token", ReleaseChannel.Public);

            string history = await service.DownloadChangelogAsync("v1.5.0", CancellationToken.None, ReleaseChannel.Dev);

            Assert.Equal(archive, history);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PublicDownloadUsesNoDevTokenAndChecksDigest()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Null(request.Headers.Authorization);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("invalid executable")) };
            }));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, "test-token", ReleaseChannel.Dev);
            var release = new GitHubRelease(new Version(98, 0, 0), "v98.0.0", "", new Uri("https://api.github.com/repos/loismedia/g-eBAR/releases/assets/1"), true,
                null, null, false, false, true, new string('0', 64), TargetChannel: ReleaseChannel.Public);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, null, CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
