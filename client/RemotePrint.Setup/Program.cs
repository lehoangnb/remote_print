using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RemotePrint.Setup;

internal static class Texts
{
    internal static string Language = "vi";
    private static readonly Dictionary<string, Dictionary<string, string>> Strings = Load();
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("translations.json")!;
        using var reader = new StreamReader(input);
        return new JavaScriptSerializer().Deserialize<Dictionary<string, Dictionary<string, string>>>(reader.ReadToEnd());
    }
    internal static string T(string text) => Strings.TryGetValue(Language, out var entries) && entries.TryGetValue(text, out var value) ? value : text;
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        if (Array.IndexOf(args, "--language") is int languageIndex && languageIndex >= 0 && languageIndex + 1 < args.Length)
            Texts.Language = args[languageIndex + 1] == "en" ? "en" : "vi";
        var server = Value(args, "--server") ?? SetupDefaults.ServerUrl();
        // Useful for staging and verifying a real server package without UAC or installation.
        if (Array.IndexOf(args, "--download-only") >= 0)
        {
            var output = Value(args, "--output");
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(server)) return 2;
            try
            {
                if (Directory.Exists(output) || File.Exists(output)) return 2;
                Task.Run(async () =>
                {
                    using var installer = new Bootstrapper();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    await installer.DownloadAsync(server, output!, new Progress<string>(), timeout.Token);
                }).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception e)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "error.txt"), e.Message);
                return 1;
            }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new SetupForm(server);
        Application.Run(form);
        return form.ExitCode;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class SetupForm : Form
{
    private readonly Label serverLabel = new() { AutoSize = true };
    private readonly TextBox server = new() { Width = 475 };
    private readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
    private readonly Button install = new() { AutoSize = true, MinimumSize = new Size(145, 34) };
    private readonly Button cancel = new() { AutoSize = true, MinimumSize = new Size(90, 34) };
    private readonly Label status = new() { AutoSize = false, Width = 475, Height = 60 };
    private readonly Label hint = new() { AutoSize = false, Width = 475, Height = 46 };
    private readonly ProgressBar progress = new() { Width = 475, Height = 8, Style = ProgressBarStyle.Marquee, Visible = false };
    private CancellationTokenSource? cancellation;
    private bool committing;
    internal int ExitCode { get; private set; }

    internal SetupForm(string serverUrl)
    {
        Text = "Remote Print Setup";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(525, 315);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        using var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("remote-print.ico")!;
        Icon = new Icon(icon);
        language.Items.AddRange(new object[] { "Tiếng Việt", "English" });
        language.SelectedIndex = Texts.Language == "en" ? 1 : 0;
        language.SetBounds(395, 18, 105, 28);
        serverLabel.Location = new Point(25, 28);
        server.SetBounds(25, 62, 475, 28);
        server.Text = serverUrl;
        hint.Location = new Point(25, 102);
        status.Location = new Point(25, 154);
        progress.Location = new Point(25, 220);
        var buttons = new FlowLayoutPanel { Location = new Point(25, 254), Size = new Size(475, 42), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { cancel, install });
        Controls.AddRange(new Control[] { serverLabel, server, language, hint, status, progress, buttons });
        AcceptButton = install;
        CancelButton = cancel;
        language.SelectedIndexChanged += (_, _) => { Texts.Language = language.SelectedIndex == 1 ? "en" : "vi"; Translate(); };
        install.Click += async (_, _) => await InstallAsync();
        cancel.Click += (_, _) => { if (cancellation != null) cancellation.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (cancellation != null) { e.Cancel = true; if (!committing) cancellation.Cancel(); } };
        Translate();
    }

    private void Translate()
    {
        serverLabel.Text = Texts.T("Địa chỉ server");
        hint.Text = Texts.T("Tải bản client mới nhất và tự cài .NET nếu thiếu. Cần quyền quản trị để cài dịch vụ.");
        install.Text = Texts.T("Tải và cài đặt");
        cancel.Text = Texts.T("Hủy");
        if (cancellation == null) status.Text = Texts.T("Sau khi cài, dán mã mời trong client để ghép nối với server.");
    }

    private async Task InstallAsync()
    {
        try { Bootstrapper.ServerUri(server.Text); }
        catch (Exception e) { status.Text = e.Message; server.Focus(); return; }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePrint", "Setup", Guid.NewGuid().ToString("N"));
        install.Enabled = server.Enabled = language.Enabled = false;
        progress.Visible = true;
        cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cancellation.Token;
        var serverUrl = server.Text;
        var reporter = new Progress<string>(value => status.Text = value);
        try
        {
            using var bootstrapper = new Bootstrapper();
            // Extraction and hashing run off the UI thread too.
            await Task.Run(() => bootstrapper.DownloadAsync(serverUrl, directory, reporter, ct));
            ct.ThrowIfCancellationRequested();
            // Cancellation must not interrupt either installer after it has started.
            committing = true;
            cancel.Enabled = false;
            await Task.Run(() => bootstrapper.EnsureRuntimeAsync(directory, reporter, ct));
            var installed = await Bootstrapper.InstallAsync(directory, reporter);
            Bootstrapper.LaunchClient(installed);
            ExitCode = 0;
            cancellation.Dispose(); cancellation = null;
            Close();
        }
        catch (Exception e)
        {
            ExitCode = 1;
            status.Text = e is OperationCanceledException ? Texts.T("Đã hủy hoặc hết thời gian cài đặt.") : e.Message;
        }
        finally
        {
            cancellation?.Dispose(); cancellation = null;
            committing = false;
            if (!IsDisposed)
            {
                progress.Visible = false;
                install.Enabled = server.Enabled = language.Enabled = cancel.Enabled = true;
            }
            // Only this setup's freshly generated staging directory is removed.
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
