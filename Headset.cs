using Windows.Devices.Enumeration;

namespace DynamicIsland;

/// <summary>Charge of Bluetooth headphones, as they report it to Windows (the number shown in Settings).</summary>
static class Headset
{
    const string Battery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery
    static readonly string[] Wanted = { Battery };

    /// <summary>Percent left in the device this container stands for, or -1 when it reports none (wired, or silent about it).</summary>
    public static async Task<int> ChargeAsync(Guid container)
    {
        if (container == Guid.Empty) return -1;
        try
        {
            // the audio endpoint knows nothing of the battery: the level sits on the Hands-Free node of the same device
            var nodes = await DeviceInformation.FindAllAsync(
                $"System.Devices.ContainerId:=\"{container:B}\"", Wanted, DeviceInformationKind.Device);
            foreach (DeviceInformation node in nodes)
                if (node.Properties.TryGetValue(Battery, out object? level) && level is byte percent && percent <= 100)
                    return percent;
        }
        catch
        {
        }
        return -1;
    }
}
