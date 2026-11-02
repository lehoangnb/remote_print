namespace RemotePrint.Client;

public sealed partial class DeviceConfigForm : Form
{
    public string ComputerName => _name.Text.Trim();
    public string ServerUrl => _url.Text.Trim();

    public DeviceConfigForm()
    {
        InitializeComponent();
        if (UiHelpers.IsDesignMode(this)) return;
        L.Apply(this, pairingToolTip);
        ActiveControl = _url;
    }

    public DeviceConfigForm(string computerName, string serverUrl, bool headless = false) : this()
    {
        _name.Text = computerName;
        _url.Text = serverUrl;
        if (headless)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-2400, -2400);
            ShowInTaskbar = false;
        }
    }
}
