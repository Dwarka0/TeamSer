using System.Collections.ObjectModel;
using TeamSpeakManager.Core.Services;

namespace TeamSpeakManager.Desktop.ViewModels;

/// <summary>Read-only network diagnostics. Explicitly never claims UDP reachability.</summary>
public sealed class NetworkViewModel : ObservableObject
{
    private readonly NetworkService _net = new();
    private readonly string _endpoint;

    private string _localIp = "—";
    private string _publicIp = "—";
    private string _binding = "";
    private string _cgnatNote = "";
    private string _firewallNote = "";

    public NetworkViewModel(string publicIpEndpoint)
    {
        _endpoint = publicIpEndpoint;
        VoicePort = 9987;
        FilePort = 30033;
    }

    public string LocalIp { get => _localIp; set => SetProperty(ref _localIp, value); }
    public string PublicIp { get => _publicIp; set => SetProperty(ref _publicIp, value); }

    public int VoicePort { get; set; }
    public int FilePort { get; set; }

    public string VoiceAddress => $"{PublicIp}:{VoicePort}";
    public string FileAddress => $"{PublicIp}:{FilePort}";

    public string Binding
    {
        get => _binding;
        set => SetProperty(ref _binding, value);
    }

    public string CgNatNote
    {
        get => _cgnatNote;
        set => SetProperty(ref _cgnatNote, value);
    }

    public string FirewallNote
    {
        get => _firewallNote;
        set => SetProperty(ref _firewallNote, value);
    }

    /// <summary>Populate diagnostics. Never mutates firewall rules or routes.</summary>
    public void Refresh()
    {
        var local = _net.GetLocalIPv4();
        LocalIp = local.Address ?? "—";
        Binding = string.IsNullOrWhiteSpace(local.InterfaceName) ? "" : local.InterfaceName;

        var value = _net.GetPublicIPv4Async(_endpoint).GetAwaiter().GetResult();
        if (value.Error is not null)
        {
            PublicIp = Strings.NoExternalIp;
            CgNatNote = value.Error;
            return;
        }

        PublicIp = value.Address ?? "—";

        CgNatNote = value.IsCarrierGradeNat
            ? Strings.CgNatWarning
            : "آدرس عمومی یافت شد. برای اتصال از بیرون باید UDP 9987 از روتور به این دستگاه ارسال شود.";
    }
}
