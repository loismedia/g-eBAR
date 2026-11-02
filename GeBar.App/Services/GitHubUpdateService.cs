using GeBar.Core;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GeBar.App;

internal sealed record GitHubRelease(Version Version, string Tag, string Changelog, Uri? DownloadUrl, bool AppUpdateAvailable, string? HidVersion, Uri? HidDownloadUrl, bool HidUpdateAvailable, bool IsNightly, bool IsDowngrade, string? Sha256 = null, string? HidSha256 = null, ReleaseChannel? TargetChannel = null);
internal readonly record struct UpdateDownloadProgress(long DownloadedBytes, long? TotalBytes);

internal sealed partial class GitHubUpdateService : IDisposable
{
    private readonly FileLogger _logger;
    private string? _lastComplaint;
    private readonly HttpClient _client;
    private readonly ReleaseChannel _currentChannel;
    private string _token;

    public GitHubUpdateService(FileLogger logger) : this(logger, new HttpClient(), null, BuildChannel.Current) { }

    internal GitHubUpdateService(FileLogger logger, HttpClient client, string? token, ReleaseChannel currentChannel)
    {
        _logger = logger;
        _client = client;
        _client.Timeout = TimeSpan.FromSeconds(15);
        _currentChannel = currentChannel;
        _token = token ?? ReadToken();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("g-eBAR", version));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private const int MaximumLogLines = 5000;
    private const int MaximumDescription = 2000;
    public bool IsConfigured => true;
    public bool HasToken => !string.IsNullOrWhiteSpace(_token);
    public bool LastCheckFailed { get; private set; }
    public UpdateAccess LastAccess { get; private set; } = UpdateAccess.Unknown;
    public static bool IsCurrentBuildNightly => bool.TryParse(
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "Nightly")?.Value,
        out bool nightly) && nightly;
    public static string CurrentVersionLabel
    {
        get
        {
            Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            string version = assemblyVersion is null ? "1.0.0" : assemblyVersion.Revision > 0 ? assemblyVersion.ToString(4) : assemblyVersion.ToString(3);
            return version + (BuildChannel.Current == ReleaseChannel.Dev ? IsCurrentBuildNightly ? " dev nightly" : " dev" : string.Empty);
        }
    }
    public static string BuildRevision
    {
        get
        {
            string? informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string[] parts = informational?.Split('+') ?? [];
            return parts.Length > 1 && parts[1].Length >= 7 ? parts[1][..7] : "local";
        }
    }
    public static bool LooksLikeToken(string value) => TokenRegex().IsMatch(value.Trim());
    public string Token => _token;

    public void SetToken(string? token)
    {
        GitHubTokenStore.Write(token);
        _token = token?.Trim() ?? string.Empty;
    }

    public (string Identifier, string Report, Uri? IssueUri, string? LogPath) CreateBugReport(string description, string settings, IReadOnlyList<string> details)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        string suffix = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 4);
        string identifier = $"g-eBUG_{version}-{IdentifierPart(Environment.MachineName)}-{suffix}";
        string? logFolder = CopyTodayLogs(identifier);
        string text = description.Trim();
        if (text.Length > MaximumDescription) text = text[..MaximumDescription] + "…";
        var builder = new StringBuilder()
            .AppendLine($"**g-eBAR** {CurrentVersionLabel} ({BuildRevision}) / **g-eHID** {GetInstalledHidVersion() ?? "not installed"} / {WindowsRelease.Describe()} ({CultureInfo.CurrentUICulture.Name})")
            .AppendLine(settings)
            .AppendLine()
            .AppendLine("**Description:**")
            .AppendLine(text.Length == 0 ? "_no description_" : text)
            .AppendLine();
        foreach (string line in details) builder.AppendLine(line);
        string report = builder.ToString();
        Uri? uri = IsConfigured
            ? new Uri($"https://github.com/{BuildChannel.Repository(_currentChannel)}/issues/new?title={Uri.EscapeDataString(identifier)}&body={Uri.EscapeDataString(report)}")
            : null;
        return (identifier, report, uri, logFolder);
    }

    private static string? CopyTodayLogs(string identifier)
    {
        try
        {
            string target = Path.Combine(Path.GetTempPath(), "g-eBAR", identifier);
            System.IO.Directory.CreateDirectory(target);
            bool collected = CopyLog(AppPaths.Logs, "g-eBAR", target) | CopyLog(AppPaths.HidLogs, "g-eHID", target);
            if (!collected) System.IO.Directory.Delete(target, true);
            return collected ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool CopyLog(string directory, string name, string target)
    {
        if (!System.IO.Directory.Exists(directory)) return false;
        string[] files = [.. System.IO.Directory.GetFiles(directory, $"{name}-{DateTime.Now:yyyy-MM-dd}*.log").Order(StringComparer.OrdinalIgnoreCase)];
        if (files.Length == 0) return false;
        string[] lines = [.. files.SelectMany(FileLogger.ReadLines)];
        File.WriteAllLines(Path.Combine(target, $"{name}.log"), lines.Length <= MaximumLogLines ? lines : lines[^MaximumLogLines..]);
        return true;
    }

    private static string IdentifierPart(string value)
    {
        string result = new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (result.Length == 0) return "PC";
        return result.Length > 32 ? result[..32] : result;
    }

    [GeneratedRegex(@"^(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public async Task<GitHubRelease?> CheckAsync(CancellationToken token, bool allowStableRollback = false, ReleaseChannel? targetChannel = null)
    {
        if (!IsConfigured) return null;
        ReleaseChannel channel = targetChannel ?? _currentChannel;
        bool switching = channel != _currentChannel;
        string repository = BuildChannel.Repository(channel);
        LastCheckFailed = true;
        try
        {
            using var request = ApiRequest($"https://api.github.com/repos/{repository}/releases?per_page=100", channel);
            using HttpResponseMessage response = await _client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastAccess = channel == ReleaseChannel.Dev ? await DiagnoseAsync(response.StatusCode, token).ConfigureAwait(false) : UpdateAccess.Unknown;
                Complain($"Update check for {repository} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}); {Explain(LastAccess)}");
                return null;
            }
            _lastComplaint = null;
            LastAccess = UpdateAccess.Granted;
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            JsonElement[] releases = document.RootElement.EnumerateArray()
                .Where(item => !item.GetProperty("draft").GetBoolean()
                    && (channel == ReleaseChannel.Dev || !item.GetProperty("prerelease").GetBoolean()))
                .ToArray();
            LastCheckFailed = false;
            var versionedReleases = releases
                .Where(item => item.GetProperty("assets").EnumerateArray().Any(asset => string.Equals(asset.GetProperty("name").GetString(), "g-eBAR.exe", StringComparison.OrdinalIgnoreCase)))
                .Select(item => (Release: item, Tag: item.GetProperty("tag_name").GetString() ?? string.Empty))
                .Select(item => (item.Release, item.Tag, Parsed: Version.TryParse(item.Tag.TrimStart('v', 'V'), out Version? parsed), Version: parsed))
                .Where(item => item.Parsed && item.Version is not null)
                .OrderByDescending(item => item.Version)
                .ToArray();
            if (versionedReleases.Length == 0) return null;
            JsonElement root = versionedReleases[0].Release;
            bool isNightly = root.GetProperty("prerelease").GetBoolean();
            string tag = versionedReleases[0].Tag;
            Version version = versionedReleases[0].Version!;
            Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
            if (current.Revision == 0) current = new Version(current.Major, current.Minor, current.Build);
            bool isDowngrade = switching && version < current;
            if (!switching && version <= current && allowStableRollback && IsCurrentBuildNightly)
            {
                foreach (var stable in versionedReleases.Where(item => !item.Release.GetProperty("prerelease").GetBoolean() && item.Version < current))
                {
                    JsonElement candidate = stable.Release;
                    string candidateTag = stable.Tag;
                    Version candidateVersion = stable.Version!;
                    if (!candidate.GetProperty("assets").EnumerateArray().Any(item => string.Equals(item.GetProperty("name").GetString(), "g-eBAR.exe", StringComparison.OrdinalIgnoreCase))) continue;
                    root = candidate;
                    tag = candidateTag;
                    version = candidateVersion;
                    isNightly = false;
                    isDowngrade = true;
                    break;
                }
            }
            bool appUpdateAvailable = switching || version > current || isDowngrade;
            JsonElement[] assets = root.GetProperty("assets").EnumerateArray().ToArray();
            JsonElement? asset = assets
                .Where(item => string.Equals(item.GetProperty("name").GetString(), "g-eBAR.exe", StringComparison.OrdinalIgnoreCase))
                .Cast<JsonElement?>()
                .FirstOrDefault();
            if (appUpdateAvailable && asset is null)
            {
                _logger.Warning($"Release {tag} has no executable asset");
                return null;
            }
            Uri? downloadUri = AssetUri(asset);
            if (appUpdateAvailable && downloadUri is null) return null;
            JsonElement? hidRelease = releases.Cast<JsonElement?>().FirstOrDefault(item =>
            {
                return item?.GetProperty("assets").EnumerateArray().Any(asset => string.Equals(asset.GetProperty("name").GetString(), "g-eHID.exe", StringComparison.OrdinalIgnoreCase)) == true;
            });
            JsonElement? hidAsset = hidRelease?.GetProperty("assets").EnumerateArray()
                .Where(item => string.Equals(item.GetProperty("name").GetString(), "g-eHID.exe", StringComparison.OrdinalIgnoreCase))
                .Cast<JsonElement?>().FirstOrDefault();
            Match hidMatch = Regex.Match(hidRelease?.GetProperty("body").GetString() ?? string.Empty, "g-eHID:\\s*(?<version>\\d{2}\\.\\d{2}-hid-[A-Za-z0-9]{5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string? hidVersion = hidMatch.Success ? hidMatch.Groups["version"].Value : null;
            Uri? hidDownloadUri = AssetUri(hidAsset);
            bool hidUpdateAvailable = hidVersion is not null && hidDownloadUri is not null
                && (switching || !string.Equals(GetInstalledHidVersion(), hidVersion, StringComparison.OrdinalIgnoreCase));
            if (switching && GetInstalledHidVersion() is not null && !hidUpdateAvailable)
                throw new InvalidOperationException("The target channel has no compatible g-eHID module.");
            if (!appUpdateAvailable && !hidUpdateAvailable) return null;
            return new(version, tag, root.GetProperty("body").GetString() ?? string.Empty, downloadUri, appUpdateAvailable, hidVersion, hidDownloadUri, hidUpdateAvailable, isNightly, isDowngrade, AssetDigest(asset), AssetDigest(hidAsset), switching ? channel : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            LastCheckFailed = true;
            if (!token.IsCancellationRequested) _logger.Error("Update check failed", ex);
            return null;
        }
    }

    private async Task<UpdateAccess> DiagnoseAsync(System.Net.HttpStatusCode status, CancellationToken token)
    {
        if (!HasToken) return UpdateAccess.NoToken;
        if (status == System.Net.HttpStatusCode.Unauthorized) return UpdateAccess.TokenRejected;
        if (status != System.Net.HttpStatusCode.NotFound && status != System.Net.HttpStatusCode.Forbidden) return UpdateAccess.Unknown;
        try
        {
            using var request = ApiRequest("https://api.github.com/user", ReleaseChannel.Dev);
            using HttpResponseMessage identity = await _client.SendAsync(request, token).ConfigureAwait(false);
            return identity.StatusCode == System.Net.HttpStatusCode.Unauthorized ? UpdateAccess.TokenRejected
                : identity.IsSuccessStatusCode ? UpdateAccess.TokenWithoutRepository
                : UpdateAccess.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return UpdateAccess.Unknown;
        }
    }

    private static string Explain(UpdateAccess access) => access switch
    {
        UpdateAccess.NoToken => "a private repository needs a GitHub token",
        UpdateAccess.TokenRejected => "GitHub rejected the token, it is expired or mistyped",
        UpdateAccess.TokenWithoutRepository => "GitHub accepted the token, but it grants no access to this repository",
        _ => "the reason is unclear, see the status code"
    };

    private void Complain(string message)
    {
        if (string.Equals(_lastComplaint, message, StringComparison.Ordinal)) return;
        _lastComplaint = message;
        _logger.Warning(message);
    }

    public async Task<string> DownloadAsync(GitHubRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        if (release.DownloadUrl is null) throw new InvalidOperationException("The release has no g-eBAR executable.");
        System.IO.Directory.CreateDirectory(AppPaths.Updates);
        string destination = Path.Combine(AppPaths.Updates, $"g-eBAR-{release.Version}.exe");
        string temporary = destination + ".download";
        try
        {
            await DownloadFileAsync(release.DownloadUrl, temporary, release.Sha256, release.TargetChannel ?? _currentChannel, progress, token).ConfigureAwait(false);
            File.Move(temporary, destination, true);
            return destination;
        }
        catch
        {
            Discard(temporary);
            throw;
        }
    }

    public async Task<string> DownloadHidAsync(GitHubRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        if (release.HidDownloadUrl is null || release.HidVersion is null) throw new InvalidOperationException("The release has no g-eHID module.");
        System.IO.Directory.CreateDirectory(AppPaths.Updates);
        string temporary = Path.Combine(AppPaths.Updates, $"g-eHID-{release.HidVersion}.exe.download");
        try
        {
            await DownloadFileAsync(release.HidDownloadUrl, temporary, release.HidSha256, release.TargetChannel ?? _currentChannel, progress, token).ConfigureAwait(false);
            return temporary;
        }
        catch
        {
            Discard(temporary);
            throw;
        }
    }

    public static void ApplyHid(string package)
    {
        System.IO.Directory.CreateDirectory(AppPaths.Modules);
        StopHidProcess();
        try { File.Move(package, AppPaths.GeHidExecutable, true); }
        catch
        {
            Discard(package);
            throw;
        }
    }

    private static void Discard(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string? GetInstalledHidVersion()
    {
        if (!File.Exists(AppPaths.GeHidExecutable)) return null;
        try { return FileVersionInfo.GetVersionInfo(AppPaths.GeHidExecutable).ProductVersion?.Split('+')[0]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void LaunchHid()
    {
        if (!File.Exists(AppPaths.GeHidExecutable)) throw new FileNotFoundException("g-eHID is not installed.", AppPaths.GeHidExecutable);
        Process.Start(new ProcessStartInfo(AppPaths.GeHidExecutable) { UseShellExecute = true });
    }

    public static void RemoveHid()
    {
        StopHidProcess();
        if (File.Exists(AppPaths.GeHidExecutable)) File.Delete(AppPaths.GeHidExecutable);
        if (System.IO.Directory.Exists(AppPaths.Modules) && !System.IO.Directory.EnumerateFileSystemEntries(AppPaths.Modules).Any()) System.IO.Directory.Delete(AppPaths.Modules);
    }

    private async Task DownloadFileAsync(Uri uri, string destination, string? sha256, ReleaseChannel channel, IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        using var request = ApiRequest(uri.AbsoluteUri, channel);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;
        progress?.Report(new UpdateDownloadProgress(0, totalBytes));
        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long downloaded = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            downloaded += count;
            progress?.Report(new UpdateDownloadProgress(downloaded, totalBytes));
        }
        if (totalBytes is long expected && downloaded != expected)
            throw new InvalidDataException($"Download of {uri} ended at {downloaded} of {expected} bytes.");
        if (sha256 is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Download of {uri} does not match the published SHA-256 digest.");
        if (sha256 is null) _logger.Warning($"Download of {uri} has no published digest, integrity not verified");
    }

    private static string? AssetDigest(JsonElement? asset) =>
        asset is JsonElement value && value.TryGetProperty("digest", out JsonElement digest) && digest.ValueKind == JsonValueKind.String
            && digest.GetString() is string text && text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? text["sha256:".Length..]
            : null;

    private static Uri? AssetUri(JsonElement? asset)
    {
        string? value = asset?.GetProperty("url").GetString();
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static void StopHidProcess()
    {
        foreach (Process process in Process.GetProcessesByName("g-eHID"))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, AppPaths.GeHidExecutable, StringComparison.OrdinalIgnoreCase)) continue;
                    process.CloseMainWindow();
                    if (!process.WaitForExit(2000)) { process.Kill(); process.WaitForExit(2000); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    public async Task<string> DownloadChangelogAsync(string? releaseTag, CancellationToken token, ReleaseChannel? targetChannel = null)
    {
        ReleaseChannel channel = targetChannel ?? _currentChannel;
        if (channel == ReleaseChannel.Dev) return await DownloadDevChangelogAsync(releaseTag, token).ConfigureAwait(false);
        using var request = ApiRequest($"https://api.github.com/repos/{BuildChannel.Repository(channel)}/releases?per_page=100", channel);
        using HttpResponseMessage response = await _client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        Version.TryParse(releaseTag?.TrimStart('v', 'V'), out Version? ceiling);
        string[] entries = document.RootElement.EnumerateArray()
            .Where(item => !item.GetProperty("draft").GetBoolean()
                && (channel == ReleaseChannel.Dev || !item.GetProperty("prerelease").GetBoolean()))
            .Select(item => (Release: item, Tag: item.GetProperty("tag_name").GetString() ?? string.Empty))
            .Select(item => (item.Release, item.Tag, Parsed: Version.TryParse(item.Tag.TrimStart('v', 'V'), out Version? version), Version: version))
            .Where(item => item.Parsed && item.Version is not null && (ceiling is null || item.Version <= ceiling))
            .OrderByDescending(item => item.Version)
            .Select(item => $"## g-eBAR {item.Tag}\n\n{item.Release.GetProperty("body").GetString()}\n")
            .ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("No published release to read the changelog from.");
        return "# Version history\n\n" + string.Join("\n", entries);
    }

    private async Task<string> DownloadDevChangelogAsync(string? releaseTag, CancellationToken token)
    {
        string repository = BuildChannel.Repository(ReleaseChannel.Dev);
        if (releaseTag is null)
        {
            using var request = ApiRequest($"https://api.github.com/repos/{repository}/releases?per_page=100", ReleaseChannel.Dev);
            using HttpResponseMessage response = await _client.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            releaseTag = document.RootElement.EnumerateArray()
                .Where(item => !item.GetProperty("draft").GetBoolean()
                    && item.GetProperty("assets").EnumerateArray().Any(asset => string.Equals(asset.GetProperty("name").GetString(), "g-eBAR.exe", StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.GetProperty("tag_name").GetString() ?? string.Empty)
                .Select(tag => (Tag: tag, Parsed: Version.TryParse(tag.TrimStart('v', 'V'), out Version? version), Version: version))
                .Where(item => item.Parsed && item.Version is not null)
                .OrderByDescending(item => item.Version)
                .Select(item => item.Tag)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No published release to read the changelog from.");
        }
        using var archiveRequest = ApiRequest($"https://api.github.com/repos/{repository}/contents/CHANGELOG.md?ref={Uri.EscapeDataString(releaseTag)}", ReleaseChannel.Dev);
        archiveRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        using HttpResponseMessage archiveResponse = await _client.SendAsync(archiveRequest, token).ConfigureAwait(false);
        archiveResponse.EnsureSuccessStatusCode();
        return await archiveResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    public static void StartInstall(string packagePath)
    {
        string currentExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the current executable path.");
        string currentFileName = Path.GetFileName(currentExecutable);
        if (!currentFileName.StartsWith("g-eBAR", StringComparison.OrdinalIgnoreCase) || !currentFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic update is available only for the published g-eBAR.exe application.");

        var startInfo = new ProcessStartInfo(packagePath) { UseShellExecute = true };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(Path.GetFullPath(packagePath));
        startInfo.ArgumentList.Add(currentExecutable);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(startInfo);
    }

    public void Dispose() => _client.Dispose();

    private string ReadToken()
    {
        try { return GitHubTokenStore.Read()?.Trim() ?? string.Empty; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.Error("Cannot read GitHub token", ex);
            return string.Empty;
        }
    }

    private HttpRequestMessage ApiRequest(string url, ReleaseChannel channel)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (channel == ReleaseChannel.Dev && HasToken && request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }
}
