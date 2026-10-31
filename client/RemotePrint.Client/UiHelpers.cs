using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace RemotePrint.Client;

// .NET 8 WinForms has neither a per-control ToolTip property (added in .NET 9) nor
// AddRange on the table-layout style collections, so the forms share one ToolTip
// component, remember each text for tests, and register styles one at a time.
public static class UiHelpers
{
    private sealed record TipText(string? Text);

    private static readonly ToolTip Tips = new();
    private static readonly ConditionalWeakTable<Control, TipText> Store = new();

    public static T SetTip<T>(this T control, string? text) where T : Control
    {
        Tips.SetToolTip(control, text);
        Store.AddOrUpdate(control, new TipText(text));
        return control;
    }

    public static string? GetTip(this Control control) =>
        Store.TryGetValue(control, out var entry) ? entry.Text : null;

    public static void AddRange(this TableLayoutRowStyleCollection styles, params RowStyle[] items)
    {
        foreach (var style in items) styles.Add(style);
    }

    public static void AddRange(this TableLayoutColumnStyleCollection styles, params ColumnStyle[] items)
    {
        foreach (var style in items) styles.Add(style);
    }
}
