using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace DynamicIsland.Core;

public record BtDeviceInfo(string Name, int? Battery);

/// <summary>Lists connected Bluetooth devices. Connection state comes from AEPs and
/// battery level from the PnP DEVPKEY. The AEP BatteryLevel query is invalid, so
/// the two sources are queried separately and then combined.</summary>
public static class BluetoothService
{
    private const string IsConnectedProp = "System.Devices.Aep.IsConnected";
    private const string BatteryKey = "{104ea319-6ee2-4701-bd47-8ddbf425bbe5} 2"; // DEVPKEY_Bluetooth_Battery
    private const string ProblemCodeKey = "{4340a6c5-93fa-4706-972c-7b648008a5a7} 3"; // DEVPKEY_Device_ProblemCode
    private const string NameProp = "System.ItemNameDisplay";
    private const string BtClassAqs = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";

    public static async Task<List<BtDeviceInfo>> GetConnectedAsync()
    {
        var connected = new List<string>();
        var props = new[] { IsConnectedProp };

        foreach (var selector in new[]
        {
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
        })
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(selector, props);
                foreach (var d in devices)
                {
                    if (string.IsNullOrWhiteSpace(d.Name)) continue;
                    if (d.Properties.TryGetValue(IsConnectedProp, out var v) && v is bool b && b &&
                        !connected.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                        connected.Add(d.Name);
                }
            }
            catch { /* BT kapalı */ }
        }

        var batteries = await GetBatteryMapAsync();

        var result = connected
            .Select(n => new BtDeviceInfo(n, FindBattery(batteries, n)))
            .ToList();

        // Devices with battery data but absent from the AEP list (for example LE mice/keyboards).
        foreach (var (name, bat) in batteries)
            if (!result.Any(r => NamesMatch(r.Name, name)))
                result.Add(new BtDeviceInfo(name, bat));

        return result;
    }

    private static async Task<Dictionary<string, int>> GetBatteryMapAsync()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pnp = await PnpObject.FindAllAsync(PnpObjectType.Device,
                new[] { NameProp, BatteryKey, ProblemCodeKey }, BtClassAqs);

            foreach (var p in pnp)
            {
                if (!p.Properties.TryGetValue(BatteryKey, out var batVal) || batVal == null) continue;
                if (!p.Properties.TryGetValue(NameProp, out var nameVal) || nameVal is not string name ||
                    string.IsNullOrWhiteSpace(name)) continue;

                // Exclude stale battery values from unavailable or faulty nodes.
                if (p.Properties.TryGetValue(ProblemCodeKey, out var prob) && prob != null &&
                    Convert.ToInt32(prob) != 0) continue;

                map[name] = Convert.ToInt32(batVal);
            }
        }
        catch { }
        return map;
    }

    private static int? FindBattery(Dictionary<string, int> map, string name)
    {
        if (map.TryGetValue(name, out var v)) return v;
        foreach (var (k, val) in map)
            if (NamesMatch(k, name)) return val;
        return null;
    }

    private static bool NamesMatch(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
        b.Contains(a, StringComparison.OrdinalIgnoreCase);
}
