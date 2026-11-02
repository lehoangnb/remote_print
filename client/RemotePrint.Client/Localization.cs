using System.Text.Json;

namespace RemotePrint.Client;

internal static class L
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePrint", "language.txt");
    private static readonly Dictionary<string, Dictionary<string, string>> Strings = Load();
    internal static string Language { get; private set; } = ReadLanguage();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "translations.json"))) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    private static string ReadLanguage()
    {
        try { return File.ReadAllText(SettingsPath).Trim() == "en" ? "en" : "vi"; }
        catch (IOException) { return "vi"; }
    }
    internal static string T(string value) => Translate(Language, value);
    // Designer files keep editable Vietnamese captions. Translate them only at runtime,
    // so the designer can round-trip its own code without executing application logic.
    internal static void Apply(Control root, ToolTip? toolTip = null)
    {
        void TranslateItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.Text = T(item.Text ?? "");
                if (item is ToolStripDropDownItem dropDown) TranslateItems(dropDown.DropDownItems);
            }
        }
        void TranslateControl(Control control)
        {
            if (control is Form or Label or Button or GroupBox) control.Text = T(control.Text);
            if (control is ToolStrip strip) TranslateItems(strip.Items);
            if (control is ListView list)
                foreach (ColumnHeader column in list.Columns) column.Text = T(column.Text);
            if (toolTip is not null)
            {
                var text = toolTip.GetToolTip(control);
                if (!string.IsNullOrEmpty(text)) toolTip.SetToolTip(control, T(text));
            }
            foreach (Control child in control.Controls) TranslateControl(child);
        }
        TranslateControl(root);
    }
    internal static string Translate(string language, string value) => Strings.TryGetValue(language, out var entries) && entries.TryGetValue(value, out var translated) ? translated : value;
    // Service messages keep their protocol text; translate known whole messages or
    // known prefixes at the UI boundary, leaving device names and OS errors intact.
    internal static string Message(string value)
    {
        if (Language != "en") return value;
        var exact = T(value);
        if (exact != value) return exact;
        if (!Strings.TryGetValue("en", out var entries)) return value;
        var prefix = entries.Keys.Where(key => key.EndsWith(' ') && value.StartsWith(key, StringComparison.Ordinal))
            .OrderByDescending(key => key.Length).FirstOrDefault();
        return prefix is null ? value : entries[prefix] + Message(value[prefix.Length..]);
    }
    internal static string Retranslate(string value, string previousLanguage, string language)
    {
        if (!Strings.TryGetValue(previousLanguage, out var entries)) return value;
        foreach (var entry in entries)
            if (entry.Value == value) return Translate(language, entry.Key);
        return value;
    }
    internal static void UseLanguage(string language) => Language = language == "en" ? "en" : "vi";
    internal static void SetLanguage(string language)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, language);
    }
}
