using System.Net;
using System.Net.Http;
using System.Text.Json;
using GeBar.App;
using GeBar.Core;

namespace GeBar.Tests;

public class DpiUpdateTests
{
    [Theory]
    [InlineData(":")]
    [InlineData("")]
    public async Task LatestModuleCanComeFromEarlierAppRelease(string separator)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-dpi-update-{Guid.NewGuid():N}");
        try
        {
            var payload = new[] { Release("99.0.1", false, separator), Release("99.0.0", true, separator) };
            using var client = new HttpClient(new Handler(JsonSerializer.Serialize(payload)));
            using var service = new GitHubUpdateService(new FileLogger(directory, "test"), client, string.Empty, ReleaseChannel.Dev);

            GitHubRelease? release = await service.CheckAsync(CancellationToken.None);

            Assert.NotNull(release);
            Assert.Equal(new Version(99, 0, 1), release.Version);
            Assert.Equal("03.11-dpi-ABCDE", release.DpiVersion);
            Assert.True(release.DpiUpdateAvailable);
            Assert.Equal("https://api.github.com/repos/loismedia/pg/releases/assets/3", release.DpiDownloadUrl?.AbsoluteUri);
            Assert.Equal(new string('C', 64), release.DpiSha256);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static object Release(string version, bool dpi, string separator) => new
    {
        tag_name = "v" + version,
        draft = false,
        prerelease = false,
        body = dpi ? $"g-eDPI{separator} 03.11-dpi-ABCDE" : "App fixes",
        assets = new[]
        {
            new { name = "g-eBAR.exe", url = "https://api.github.com/repos/loismedia/pg/releases/assets/1", digest = "sha256:" + new string('A', 64) },
            new { name = dpi ? "g-eDPI.dll" : "other.bin", url = "https://api.github.com/repos/loismedia/pg/releases/assets/3", digest = "sha256:" + new string('C', 64) }
        }
    };

    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
