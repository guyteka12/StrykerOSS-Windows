using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real USB enumeration via one-shot PowerShell processes (Get-PnpEntity / Get-CimInstance)
/// plus real ISO mounting (Mount-DiskImage) and Shell COM eject. No System.Management.
/// </summary>
public sealed class UsbService : IUsbService
{
    private readonly ILogger _log;

    public UsbService(ILogger log) => _log = log;

    // ---------------------------------------------------------- enumeration

    public async Task<List<UsbDeviceInfo>> ListUsbDevicesAsync()
    {
        var result = new List<UsbDeviceInfo>();

        // 1) removable storage volumes (DriveType=2) first
        try
        {
            const string script = @"Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=2' | Select-Object DeviceID, VolumeName, Size, FileSystem | ConvertTo-Json -Compress";
            var (_, @out, _) = await RunPowerShellAsync(script, 30);
            foreach (var el in ParseJsonArray(@out))
            {
                var letter = Str(el, "DeviceID");
                if (string.IsNullOrWhiteSpace(letter)) continue;
                var volume = Str(el, "VolumeName");
                result.Add(new UsbDeviceInfo(
                    PnpId: $"VOLUME:{letter}",
                    Name: string.IsNullOrWhiteSpace(volume) ? "Lecteur amovible" : volume,
                    Manufacturer: null,
                    Class: "Disk",
                    IsStorage: true,
                    DriveLetter: letter,
                    SizeBytes: Num(el, "Size")));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("usb", $"removable volume enumeration failed: {ex.Message}");
        }

        // 2) present PnP entities on the USB bus
        try
        {
            const string script = @"$ErrorActionPreference='SilentlyContinue'; Get-PnpEntity -PresentOnly | Where-Object { $_.InstanceId -like 'USB\*' } | Select-Object -First 80 Class, FriendlyName, Manufacturer, InstanceId | ConvertTo-Json -Compress";
            var (_, @out, _) = await RunPowerShellAsync(script, 60);
            foreach (var el in ParseJsonArray(@out))
            {
                var pnp = Str(el, "InstanceId");
                if (string.IsNullOrWhiteSpace(pnp)) continue;
                result.Add(new UsbDeviceInfo(
                    PnpId: pnp,
                    Name: Str(el, "FriendlyName"),
                    Manufacturer: Str(el, "Manufacturer"),
                    Class: Str(el, "Class") ?? "USB",
                    IsStorage: false,
                    DriveLetter: null,
                    SizeBytes: null));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("usb", $"PnP USB enumeration failed: {ex.Message}");
        }

        _log.Info("usb", $"enumerated {result.Count} entries ({result.Count(d => d.IsStorage)} removable volumes)");
        return result;
    }

    // ---------------------------------------------------------- mount / eject

    public async Task<bool> MountIsoAsync(string isoPath, IProgress<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
        {
            log?.Report($"ISO introuvable : {isoPath}");
            _log.Warn("usb", $"ISO mount failed - file not found: {isoPath}");
            return false;
        }

        var escaped = isoPath.Replace("'", "''"); // single quotes doubled for PS string literal
        var script = "$ErrorActionPreference='Stop'; $r = Mount-DiskImage -ImagePath '" + escaped + "' -PassThru; ($r | Get-Volume).DriveLetter";

        log?.Report($"Montage de '{Path.GetFileName(isoPath)}' via Mount-DiskImage ...");
        var (code, @out, err) = await RunPowerShellAsync(script, 60);
        var letter = (@out ?? string.Empty).Trim();

        if (code == 0 && letter.Length > 0)
        {
            log?.Report($"'{Path.GetFileName(isoPath)}' monté en tant que lecteur {letter}:");
            _log.Info("usb", $"mounted ISO '{isoPath}' -> {letter}:");
            return true;
        }

        var msg = string.IsNullOrWhiteSpace(err) ? "aucune lettre de lecteur retournée (l'image est peut-être corrompue ou déjà montée)" : err.Trim();
        log?.Report($"Échec du montage : {msg}");
        _log.Error("usb", $"ISO mount failed for '{isoPath}': {msg}");
        return false;
    }

    public async Task<bool> EjectDriveAsync(string driveLetter, IProgress<string>? log = null)
    {
        var dl = (driveLetter ?? string.Empty).Trim().TrimEnd('\\', ':', ' ');
        if (dl.Length != 1 || !char.IsLetter(dl[0]))
        {
            log?.Report($"Lettre de lecteur invalide : '{driveLetter}'");
            _log.Warn("usb", $"eject rejected - invalid drive letter '{driveLetter}'");
            return false;
        }

        // Shell.Application InvokeVerb('Eject') is the same verb Explorer uses for
        // "Eject" on removable drives; it is asynchronous and has no exit code.
        var script = "(New-Object -ComObject Shell.Application).NameSpace(17).ParseName('" + dl + ":\\').InvokeVerb('Eject')";
        log?.Report($"Envoi du verbe d'éjection à {dl}: (Shell.Application COM) ...");
        var (code, @out, err) = await RunPowerShellAsync(script, 30);

        var stderr = string.IsNullOrWhiteSpace(err) ? string.Empty : $", stderr: {err.Trim()}";
        log?.Report($"Demande d'éjection envoyée pour {dl}: - le verbe COM est asynchrone, vérifiez dans l'Explorateur que le lecteur a disparu avant de le débrancher.");
        _log.Info("usb", $"eject verb sent for {dl}: (powershell exit {code}{stderr})");
        return true;
    }

    // ---------------------------------------------------------- helpers

    /// <summary>One-shot real powershell.exe run; returns (exitcode, stdout, stderr).</summary>
    private static async Task<(int Code, string Out, string Err)> RunPowerShellAsync(string script, int timeoutSec = 30)
    {
        var encoded = script.Replace("\"", "\\\"");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"" + encoded + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Échec du démarrage de powershell.exe.");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            return (-1, await outTask, "délai d'attente dépassé pour powershell");
        }

        return (p.ExitCode, await outTask, await errTask);
    }

    /// <summary>Parses ConvertTo-Json -Compress output: single object or array (PowerShell returns a bare object for one result).</summary>
    private static List<JsonElement> ParseJsonArray(string json)
    {
        var list = new List<JsonElement>();
        var trimmed = (json ?? string.Empty).Trim();
        if (trimmed.Length == 0) return list; // zero results -> empty output
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (trimmed[0] == '{')
            {
                list.Add(doc.RootElement.Clone());
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                    list.Add(item.Clone());
            }
        }
        catch (JsonException)
        {
            // non-JSON output (warnings etc.) -> treat as no results
        }
        return list;
    }

    private static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? Num(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
