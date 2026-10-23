using System.Windows;

namespace Reclaimer.Features;

/// <summary>history.jsonl as a list (v1.0): what was moved, restored, deleted, and how much space came back.</summary>
public partial class HistoryPage
{
    public HistoryPage()
    {
        InitializeComponent();
        DataContext = Model.I;
        Copy.Attach(List, o => o is HistoryRow r ? (r.NameText, string.Join(Environment.NewLine, r.Paths), null) : null);
        Loaded += (_, _) =>
        {
            Model.I.RefreshHistory();
            EmptyText.Visibility = Model.I.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
    }
}
