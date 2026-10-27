using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DbSchemaSyncAuditor.Services;

public record UpdateCheckResult(
    bool UpdateAvailable,
    string? LatestVersion,
    string? DownloadUrl,
    string? ReleaseNotes
);

public class AppUpdateService
{
    private const string RepoOwner = "ylbani";
    private const string RepoName = "corrector_db_lacak";
    private readonly HttpClient _http;

    public AppUpdateService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DbSchemaSyncAuditor", "1.0"));
    }

    public Version GetCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetName().Version ?? new Version(1, 0, 0, 0);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Si aún no hay release formal con tag, no hay update disponible por release API
                return new UpdateCheckResult(false, null, null, null);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            var releaseNotes = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() : null;

            // Extraer versión limpia (ej: "v1.0.1" -> "1.0.1")
            var cleanVersionStr = tagName.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(cleanVersionStr, out var latestVer))
            {
                return new UpdateCheckResult(false, null, null, null);
            }

            var currentVer = GetCurrentVersion();
            var isNewer = latestVer > currentVer;

            // Buscar asset del instalador (.exe)
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (asset.TryGetProperty("browser_download_url", out var dl))
                        {
                            downloadUrl = dl.GetString();
                            break;
                        }
                    }
                }
            }

            return new UpdateCheckResult(
                UpdateAvailable: isNewer && !string.IsNullOrEmpty(downloadUrl),
                LatestVersion: cleanVersionStr,
                DownloadUrl: downloadUrl,
                ReleaseNotes: releaseNotes
            );
        }
        catch
        {
            return new UpdateCheckResult(false, null, null, null);
        }
    }

    public async Task DownloadAndExecuteInstallerAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tempSetupPath = Path.Combine(Path.GetTempPath(), $"DbSchemaSyncAuditor_Setup_{Guid.NewGuid():N}.exe");

        using (var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var srcStream = await response.Content.ReadAsStreamAsync(ct);
            await using var destStream = new FileStream(tempSetupPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            var bytesRead = 0;
            long totalRead = 0;

            while ((bytesRead = await srcStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await destStream.WriteAsync(buffer, 0, bytesRead, ct);
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    progress?.Report((double)totalRead / totalBytes);
                }
            }
        }

        // Ejecutar el instalador descargado con elevación si es requerida
        var psi = new ProcessStartInfo
        {
            FileName = tempSetupPath,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        };

        Process.Start(psi);

        // Salir de la aplicación para permitir reemplazo de archivos
        Environment.Exit(0);
    }
}
