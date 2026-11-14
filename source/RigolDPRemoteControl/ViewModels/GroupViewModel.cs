using System.Windows.Input;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Settings;

namespace RigolDPRemoteControl.ViewModels;

/// <summary>A user defined set of channels (possibly on different power supplies) that is switched together.</summary>
public sealed class GroupViewModel : ObservableObject
{
    public GroupViewModel(MainViewModel main, GroupSettings settings)
    {
        Main = main;
        Settings = settings;
        OnCommand = new RelayCommand(() => Main.Switch(ResolveMembers(), true, useDelays: true), () => ResolveMembers().Any(c => c.Device.IsConnected));
        OffCommand = new RelayCommand(() => Main.Switch(ResolveMembers(), false, useDelays: true), () => ResolveMembers().Any(c => c.Device.IsConnected));
    }

    public MainViewModel Main { get; }
    public GroupSettings Settings { get; }

    public string Name
    {
        get => Settings.Name;
        set { Settings.Name = value; OnPropertyChanged(); Main.RequestSave(); }
    }

    public ICommand OnCommand { get; }
    public ICommand OffCommand { get; }

    public List<ChannelViewModel> ResolveMembers()
        => Settings.Members.Select(Main.FindChannel).Where(c => c != null).Cast<ChannelViewModel>().ToList();

    private string _summary = "";
    public string Summary { get => _summary; private set => SetField(ref _summary, value); }

    private string _membersText = "";
    public string MembersText { get => _membersText; private set => SetField(ref _membersText, value); }

    /// <summary>0 = all off, 1 = partially on, 2 = all on.</summary>
    private int _stateLevel;
    public int StateLevel { get => _stateLevel; private set => SetField(ref _stateLevel, value); }

    internal void UpdateUi()
    {
        var m = ResolveMembers();
        int on = m.Count(c => c.IsOutputOn);
        Summary = m.Count == 0 ? "no channels" : $"{on}/{m.Count} on";
        StateLevel = m.Count == 0 || on == 0 ? 0 : on == m.Count ? 2 : 1;
        MembersText = string.Join(", ", m.Select(c => c.DisplayName + " @" + c.Device.Host));
    }
}
