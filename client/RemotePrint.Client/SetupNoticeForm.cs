namespace RemotePrint.Client;

public sealed partial class SetupNoticeForm : Form
{
    public SetupNoticeForm()
    {
        InitializeComponent();
        if (UiHelpers.IsDesignMode(this)) return;
        L.Apply(this);
    }

    public SetupNoticeForm(bool headless) : this()
    {
        if (headless)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-2400, -2400);
            ShowInTaskbar = false;
        }
    }
}
