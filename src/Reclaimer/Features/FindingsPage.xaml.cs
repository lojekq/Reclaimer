using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Reclaimer.Shell;

namespace Reclaimer.Features;

public partial class FindingsPage
{
    /// <summary>Set by a tile on the Overview page before navigating here.</summary>
    public static Category? CategoryFilter;

    readonly ListCollectionView view = new(Model.I.Units);

    public FindingsPage()
    {
        InitializeComponent();
        DataContext = Model.I;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Unit.CategoryText)));
        view.SortDescriptions.Add(new(nameof(Unit.Category), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new(nameof(Unit.Bytes), ListSortDirection.Descending));
        view.Filter = o => Passes((Unit)o);
        Grid.ItemsSource = view;

        AgeBox.PlaceholderText = Settings.Current.AgeDays.ToString();
        AgeBox.Value = AgeFilter;
        SizeBox.Value = Settings.Current.MinSizeMb;
        CategoryBox.Items.Add(Loc.T("Findings.All"));
        foreach (var c in Filters) CategoryBox.Items.Add(Loc.T("Category." + c));
        CategoryBox.SelectedIndex = CategoryFilter is { } cf ? Array.IndexOf(Filters, cf) + 1 : 0;
        CategoryFilter = null;

        AgeBox.ValueChanged += (_, _) => { AgeFilter = AgeBox.Value ?? 0; Refresh(); };
        SizeBox.ValueChanged += (_, _) => Refresh();
        CategoryBox.SelectionChanged += (_, _) => Refresh();
        Grid.SelectionChanged += (_, _) => ShowDetails(Grid.SelectedItem as Unit);
        Copy.Attach(Grid, o => o is Unit u ? (u.Title, u.Path, u.Origin) : null);
        ScanButton.Click += (_, _) => _ = Model.I.ScanAsync();
        RecommendButton.Click += (_, _) => Model.I.MarkRecommended(view.Cast<Unit>());
        RecheckButton.Click += (_, _) => Model.I.RecheckOwners();
        ExportButton.Click += (_, _) => Report.Export();
        MoveButton.Click += (_, _) => _ = Dialog.Confirm();
        ExplorerButton.Click += (_, _) =>
        {
            if (Args.Current.Demo || Grid.SelectedItem is not Unit u) return;
            Process.Start("explorer.exe", $"/select,\"{u.Path}\"");
        };
        Loaded += (_, _) => { Model.I.PropertyChanged += ModelChanged; Model.I.Units.CollectionChanged += UnitsChanged; Sync(); };
        Unloaded += (_, _) => { Model.I.PropertyChanged -= ModelChanged; Model.I.Units.CollectionChanged -= UnitsChanged; };
        ShowDetails(null);
    }

    /// <summary>The age filter survives page changes; 0 = off (the threshold for "Mark recommended" is in Settings).</summary>
    static double AgeFilter;

    static readonly Category[] Filters = [Category.Models, Category.OwnModels, Category.Caches, Category.Projects];

    /// <summary>For the GIF scenario.</summary>
    public void SetAge(int days) => AgeBox.Value = days;

    bool Passes(Unit u) =>
        (AgeFilter <= 0 || u.AgeDays >= AgeFilter)
        && u.Bytes >= (SizeBox.Value ?? 0) * (1 << 20)
        && (CategoryBox.SelectedIndex <= 0 || u.Category == Filters[CategoryBox.SelectedIndex - 1]);

    void Refresh()
    {
        view.Refresh();
        Sync();
    }

    void ModelChanged(object? s, PropertyChangedEventArgs e) => Sync();
    void UnitsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Sync();

    void Sync()
    {
        Ring.Visibility = Model.I.Scanning ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = view.IsEmpty && !Model.I.Scanning ? Visibility.Visible : Visibility.Collapsed;
        MoveButton.IsEnabled = Model.I.AnySelected && Model.I.Idle;
        ExportButton.IsEnabled = Model.I.Units.Count > 0 && !Model.I.Scanning;
    }

    void ShowDetails(Unit? u)
    {
        Details.Visibility = u is null ? Visibility.Collapsed : Visibility.Visible;
        if (u is null) return;
        DetailPath.Text = string.Join("\n", u.Parts.Count > 4 ? [u.Path, string.Format(Loc.T("Confirm.Parts"), u.Parts.Count)] : u.Parts.Select(p => p.Path));
        var lines = new List<string>();
        if (u.LastUsed is { } t) lines.Add(string.Format(Loc.T("Details.Used"), t.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentUICulture), u.SourceText));
        if (u.Marker is not null) lines.Add(string.Format(Loc.T("Details.Marker"), u.Marker));
        if (u.Origin is not null) lines.Add(string.Format(Loc.T("Details.Origin"), u.Origin));
        else if (u.OriginUnknown) lines.Add(Loc.T("Rebuild.UnknownTip"));
        lines.AddRange(u.Notes);
        if (u.Status == UnitStatus.OwnerRunning) lines.Add(string.Format(Loc.T("Details.CloseOwner"), u.Owner));
        else if (u.Status == UnitStatus.InUse) lines.Add(string.Format(Loc.T("Details.InUse"), u.Owner));
        else if (!u.CanSelect) lines.Add(Loc.T("Details.Why." + u.Status));
        DetailText.Text = string.Join(" · ", lines);
    }
}
