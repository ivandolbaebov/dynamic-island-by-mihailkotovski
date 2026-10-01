using System.Net.NetworkInformation;
using System.Windows.Threading;
using Windows.Networking.Connectivity;
using Connectivity = Windows.Networking.Connectivity.NetworkInformation;

namespace DynamicIsland;

/// <summary>Watches the internet connection and VPN tunnels, and reports each change once it has settled.</summary>
sealed class NetworkService
{
    public enum Link { None, Wifi, Wired, Mobile }

    /// <param name="Name">Wi-Fi network name, or the connection profile name for the other links.</param>
    /// <param name="Vpn">Names of the tunnels that are up, one per line.</param>
    public readonly record struct State(Link Link, string Name, bool Internet, string Vpn);

    // a reconnect goes through several states in a row; only where it lands is worth showing
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);
    const int PropVirtual = 53; // interface type of WireGuard / Wintun style tunnels
    static readonly string[] VpnHints = { "VPN", "TAP-", "OpenVPN" };

    readonly Dispatcher _ui;
    readonly DispatcherTimer _settle;
    State _state;
    bool _reading;

    public NetworkService(Dispatcher ui)
    {
        _ui = ui;
        _settle = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = RefreshAsync();
        };
    }

    /// <summary>Raised on the UI thread with the previous and the new state.</summary>
    public event Action<State, State>? Changed;

    public async Task StartAsync()
    {
        _state = await Task.Run(Read);
        Connectivity.NetworkStatusChanged += _ => Poke();
        NetworkChange.NetworkAddressChanged += (_, _) => Poke();
    }

    void Poke() => _ui.InvokeAsync(() =>
    {
        _settle.Stop();
        _settle.Start();
    });

    async Task RefreshAsync()
    {
        if (_reading)
        {
            // something changed mid-read: look again once this one is done
            _settle.Start();
            return;
        }

        _reading = true;
        State now;
        try { now = await Task.Run(Read); }
        finally { _reading = false; }

        if (now == _state) return;
        State was = _state;
        _state = now;
        Changed?.Invoke(was, now);
    }

    static State Read()
    {
        Link link = Link.None;
        string name = "";
        bool internet = false;
        try
        {
            ConnectionProfile? profile = Connectivity.GetInternetConnectionProfile();
            if (profile != null)
            {
                link = profile.IsWlanConnectionProfile ? Link.Wifi : profile.IsWwanConnectionProfile ? Link.Mobile : Link.Wired;
                name = profile.ProfileName ?? "";
                internet = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return new State(link, name, internet, Tunnels());
    }

    static string Tunnels()
    {
        try
        {
            var up = new List<string>();
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                bool tunnel = n.NetworkInterfaceType == NetworkInterfaceType.Ppp
                    || (int)n.NetworkInterfaceType == PropVirtual
                    || VpnHints.Any(hint => n.Description.Contains(hint, StringComparison.OrdinalIgnoreCase));
                if (tunnel) up.Add(n.Name);
            }
            up.Sort(StringComparer.Ordinal);
            return string.Join('\n', up);
        }
        catch
        {
            return "";
        }
    }
}
