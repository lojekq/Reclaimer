using System.Windows;

namespace Reclaimer.Features;

public partial class DisksPage
{
    public DisksPage()
    {
        InitializeComponent();
        DataContext = Model.I;
        Loaded += (_, _) => EmptyText.Visibility = Model.I.Disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
