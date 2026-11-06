using System.Collections.Concurrent;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real Bluetooth LE discovery via the Windows BLE stack (WinRT BluetoothLEAdvertisementWatcher).
/// Decodes Google Fast Pair (company id 0x00E0) model IDs from real advertisement payloads.
/// </summary>
public sealed class BleService : IBleService
{
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, BleDevice> _devices = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private Action<BleDevice>? _foundCallback;

    public bool IsSupported { get; }

    public BleService(ILogger log)
    {
        _log = log;
        try
        {
            var radios = Radio.GetRadiosAsync().GetAwaiter().GetResult();
            IsSupported = radios.Any(r => r.Kind == RadioKind.Bluetooth && r.State == RadioState.On);
        }
        catch
        {
            IsSupported = false;
        }
        _log.Info("ble", $"BLE service created, supported={IsSupported}");
    }

    public event Action<BleDevice>? Found;

    public Task StartAsync(IProgress<string>? log = null)
    {
        if (!IsSupported)
        {
            const string msg = "Bluetooth radio is off or unavailable - enable Bluetooth in Windows settings.";
            log?.Report(msg);
            _log.Warn("ble", "scan requested but no Bluetooth radio is On");
            return Task.CompletedTask;
        }
        if (_watcher != null)
        {
            log?.Report("BLE scan already running.");
            return Task.CompletedTask;
        }

        _foundCallback = Found;
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            watcher.Received -= OnReceived;
            _log.Error("ble", "failed to start advertisement watcher", ex);
            log?.Report($"Failed to start BLE watcher: {ex.Message}");
            return Task.CompletedTask;
        }
        _watcher = watcher;
        log?.Report("BLE active scan started.");
        _log.Info("ble", "BluetoothLEAdvertisementWatcher started (Active)");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_watcher == null) return Task.CompletedTask;
        try
        {
            _watcher.Received -= OnReceived;
            _watcher.Stop();
        }
        catch (Exception ex)
        {
            _log.Warn("ble", $"watcher stop error: {ex.Message}");
        }
        _watcher = null;
        _log.Info("ble", "BluetoothLEAdvertisementWatcher stopped");
        return Task.CompletedTask;
    }

    public IReadOnlyList<BleDevice> Snapshot()
        => _devices.Values.OrderByDescending(d => d.LastSeen).ToList();

    public async Task<string> EnumerateGattAsync(string address, IProgress<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            ulong addr = Convert.ToUInt64(address.Replace(":", ""), 16);
            log?.Report($"Connecting to {address} ...");
            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
            if (dev == null)
            {
                string err = $"ERROR: cannot connect to {address} (device not present or not connectable).";
                log?.Report(err);
                return err;
            }

            var sb = new StringBuilder();
            using (dev)
            {
                sb.AppendLine($"Device: {(string.IsNullOrWhiteSpace(dev.Name) ? "(unnamed)" : dev.Name)} ({address})");
                var services = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (services.Status != GattCommunicationStatus.Success)
                    sb.AppendLine($"Services query status: {services.Status}");
                sb.AppendLine($"Services: {services.Services.Count}");
                foreach (var service in services.Services)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        sb.AppendLine($"  Service {service.Uuid}");
                        var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                        if (chars.Status != GattCommunicationStatus.Success)
                            sb.AppendLine($"    characteristics status: {chars.Status}");
                        foreach (var c in chars.Characteristics)
                        {
                            ct.ThrowIfCancellationRequested();
                            sb.AppendLine($"    Char {c.Uuid} [{DescribeProperties(c.CharacteristicProperties)}]");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"    (service enumeration error: {ex.Message})");
                    }
                }
            }

            string report = sb.ToString();
            log?.Report("GATT enumeration complete.");
            _log.Info("ble", $"GATT enumerated {address}");
            return report;
        }
        catch (OperationCanceledException)
        {
            log?.Report("GATT enumeration cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            string err = $"ERROR: GATT enumeration failed: {ex.Message}";
            log?.Report(err);
            _log.Error("ble", "GATT enumeration failed", ex);
            return err;
        }
    }

    // ---- advertisement handling (non-UI thread, fine) ----

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            string address = FormatAddress(args.BluetoothAddress);
            string? name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? null : args.Advertisement.LocalName;
            short rssi = args.RawSignalStrengthInDBm;

            bool isFastPair = false;
            string? modelId = null;
            string? company = null;

            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId == 0x00E0)
                {
                    // Google Fast Pair: model ID is 3 bytes; extended frames carry a
                    // leading frame byte before the model id. Read the real payload.
                    var parsed = ReadFastPairModelId(md.Data);
                    if (parsed != null)
                    {
                        isFastPair = true;
                        modelId = parsed;
                        company = "Google";
                    }
                }
                else if (company == null)
                {
                    company = md.CompanyId.ToString("X4");
                }
            }

            var dev = new BleDevice(address, name, rssi, isFastPair, modelId, company, DateTime.Now, DateTime.Now, null);
            var merged = _devices.AddOrUpdate(address, dev,
                (_, old) => old with
                {
                    Name = name ?? old.Name,
                    Rssi = rssi > old.Rssi ? rssi : old.Rssi,
                    IsFastPair = isFastPair || old.IsFastPair,
                    ModelId = modelId ?? old.ModelId,
                    Company = company ?? old.Company,
                    LastSeen = DateTime.Now
                });

            var cb = _foundCallback ?? Found;
            cb?.Invoke(merged);
        }
        catch (Exception ex)
        {
            _log.Warn("ble", $"advertisement parse error: {ex.Message}");
        }
    }

    /// <summary>Parses the real Google Fast Pair service data payload (model id = 3 bytes, big-endian, uppercase hex).</summary>
    private static string? ReadFastPairModelId(IBuffer? buffer)
    {
        if (buffer == null || buffer.Length < 3) return null;
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[checked((int)reader.UnconsumedBufferLength)];
        reader.ReadBytes(bytes);
        var id = bytes.Length >= 4 ? bytes.Skip(1).Take(3).ToArray() : bytes.Take(3).ToArray();
        if (id.All(b => b == 0)) return null;
        return Convert.ToHexString(id);
    }

    private static string FormatAddress(ulong bluetoothAddress)
    {
        // WinRT address is a ulong; MAC octets are the bytes in reverse order.
        var bytes = BitConverter.GetBytes(bluetoothAddress);
        return $"{bytes[0]:X2}:{bytes[1]:X2}:{bytes[2]:X2}:{bytes[3]:X2}:{bytes[4]:X2}:{bytes[5]:X2}";
    }

    private static string DescribeProperties(GattCharacteristicProperties p)
    {
        var flags = new List<string>();
        if (p.HasFlag(GattCharacteristicProperties.Broadcast)) flags.Add("Broadcast");
        if (p.HasFlag(GattCharacteristicProperties.Read)) flags.Add("Read");
        if (p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) flags.Add("WriteWithoutResponse");
        if (p.HasFlag(GattCharacteristicProperties.Write)) flags.Add("Write");
        if (p.HasFlag(GattCharacteristicProperties.Notify)) flags.Add("Notify");
        if (p.HasFlag(GattCharacteristicProperties.Indicate)) flags.Add("Indicate");
        if (p.HasFlag(GattCharacteristicProperties.AuthenticatedSignedWrites)) flags.Add("SignedWrites");
        if (p.HasFlag(GattCharacteristicProperties.ExtendedProperties)) flags.Add("Extended");
        return flags.Count == 0 ? "-" : string.Join("|", flags);
    }
}
