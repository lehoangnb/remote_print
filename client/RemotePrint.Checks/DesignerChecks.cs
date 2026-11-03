using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RemotePrint.Client;

internal static partial class Program
{
    private static void CheckDesignerForms(string root)
    {
        Application.EnableVisualStyles();
        var originalLanguage = L.Language;
        var originalContext = LicenseManager.CurrentContext;
        var types = new[] { typeof(MainForm), typeof(DeviceConfigForm), typeof(SetupNoticeForm),
            typeof(InstalledPrintersForm), typeof(TestPrintFilesForm), typeof(UpdateProgressForm) };
        var directory = Path.Combine(root, "artifacts", "designer-layout");
        Directory.CreateDirectory(directory);
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
        try
        {
            L.UseLanguage("en");
            LicenseManager.CurrentContext = new DesigntimeLicenseContext();
            foreach (var type in types)
            {
                using var form = (Form)Activator.CreateInstance(type)!;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-2400, -2400);
                form.ShowInTaskbar = false;
                form.Show();
                Application.DoEvents();
                // SplitContainer owns two framework-created panels, which are not
                // fields serialized by InitializeComponent.
                var controls = Descendants(form).Where(control => control is not SplitterPanel).ToList();
                Require(controls.Count > 0 && controls.All(control => !string.IsNullOrWhiteSpace(control.Name)),
                    type.Name + " exposes named controls in design mode");
                Require(controls.Select(control => control.Name).Distinct().Count() == controls.Count,
                    type.Name + " has unique control names for Designer serialization");
                if (form is MainForm main)
                {
                    main.StartAgent();
                    var timer = (System.Windows.Forms.Timer)type.GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                    Require(!timer.Enabled && main.ServiceStatusText.Text == "Đang kiểm tra…",
                        "Design mode starts no service polling and keeps editable Vietnamese captions");
                }
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, type.Name + ".png"));
                form.Close();
            }

            LicenseManager.CurrentContext = originalContext;
            foreach (var language in new[] { "vi", "en" })
            {
                L.UseLanguage(language);
                foreach (var type in types)
                {
                    using var form = (Form)Activator.CreateInstance(type)!;
                    foreach (var button in Descendants(form).OfType<Button>())
                        Require(TextRenderer.MeasureText(button.Text, button.Font).Width + 8 <= button.Width,
                            $"{type.Name} {language} button fits: {button.Text}");
                    if (form is MainForm main)
                    {
                        var timer = (System.Windows.Forms.Timer)type.GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                        Require(!timer.Enabled, "Constructing MainForm does not connect to the service");
                    }
                    if (form is DeviceConfigForm config)
                        Require(config.SaveButton.Text == L.T("Lưu và kết nối"), "Configuration caption follows startup language");
                    if (form is TestPrintFilesForm)
                    {
                        var list = Descendants(form).OfType<ListView>().Single();
                        Require(list.Columns[0].Text == L.T("Thời gian nhận"), "File columns follow startup language");
                    }
                    if (form is UpdateProgressForm progress)
                    {
                        using (var owner = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(100, 100, 800, 600), ShowInTaskbar = false })
                        using (var centered = new UpdateProgressForm())
                        {
                            owner.Show();
                            centered.Show(owner);
                            Application.DoEvents();
                            var working = Screen.FromControl(owner).WorkingArea;
                            var expectedX = Math.Clamp(owner.Left + (owner.Width - centered.Width) / 2, working.Left, Math.Max(working.Left, working.Right - centered.Width));
                            var expectedY = Math.Clamp(owner.Top + (owner.Height - centered.Height) / 2, working.Top, Math.Max(working.Top, working.Bottom - centered.Height));
                            Require(centered.Location == new Point(expectedX, expectedY), "Progress window is centered on its owner within the screen");
                            centered.ShowPrinterInstallation();
                            Require(!Descendants(centered).OfType<Button>().Single().Visible && Descendants(centered).OfType<ProgressBar>().Single().Style == ProgressBarStyle.Marquee,
                                "Printer installation shows an indeterminate progress indicator without cancellation");
                            foreach (var (show, title, status) in new (Action, string, string)[] {
                                (centered.ShowPrinterRemoval, "Xóa máy in", "Đang xóa máy in, vui lòng chờ…"),
                                (centered.ShowServiceInstallation, "Cài đặt dịch vụ", "Đang cài đặt dịch vụ; hãy xác nhận yêu cầu quyền quản trị nếu có…"),
                                (centered.ShowServiceRemoval, "Gỡ cài đặt dịch vụ", "Đang gỡ dịch vụ; hãy xác nhận yêu cầu quyền quản trị nếu có…"),
                                (centered.ShowClientRemoval, "Gỡ cài đặt ứng dụng", "Đang gỡ ứng dụng; hãy xác nhận yêu cầu quyền quản trị nếu có…") })
                            {
                                show();
                                var label = Descendants(centered).OfType<Label>().Single();
                                Require(centered.Text == L.T(title) && label.Text == L.T(status) && (language != "en" || L.T(status) != status),
                                    $"{language} operation progress displays the translated status: {title}");
                                Require(TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak).Height <= label.Height,
                                    $"{language} operation status fits: {title}");
                            }
                        }
                        progress.StartPosition = FormStartPosition.Manual;
                        progress.Location = new Point(-2400, -2400);
                        progress.ShowInTaskbar = false;
                        progress.Show();
                        var cancelCount = 0;
                        progress.CancelRequested += (_, _) => cancelCount++;
                        var cancel = Descendants(form).OfType<Button>().Single();
                        Require(cancel.Text == (language == "en" ? "Cancel" : "Hủy"), "Update cancel caption follows startup language");
                        cancel.PerformClick();
                        Require(cancelCount == 1, "Update cancellation still reaches the updater");
                        progress.ReportProgress(64);
                        Require(Descendants(form).OfType<ProgressBar>().Single().Value == 64,
                            "Update download progress reaches the Designer control");
                        progress.ShowVerification();
                        Require(Descendants(form).OfType<Label>().Single().Text == L.T("Đang kiểm tra gói cập nhật…"),
                            "Update verification status follows the selected language");
                        progress.ShowInstallation();
                        Require(!cancel.Enabled, "Installation disables cancellation");
                    }
                }
            }
        }
        finally
        {
            LicenseManager.CurrentContext = originalContext;
            L.UseLanguage(originalLanguage);
        }
    }
}
