// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace Grog.App.Controls;

/// <summary>
/// The body of a column-header filter popup: heading, one checkbox per <c>FilterOption</c> (with its legend
/// swatch when it has one), a rule, then All / None. Four Library headers (storage, extras, account, status)
/// each carried a hand-built copy before 09-02.
/// </summary>
public sealed class FilterPanel : TemplatedControl
{
    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<FilterPanel, string?>(nameof(Heading));
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<FilterPanel, IEnumerable?>(nameof(ItemsSource));
    /// <summary>The command parameter naming the column ("folder", "extras", "owner", "status").</summary>
    public static readonly StyledProperty<string?> FilterKeyProperty =
        AvaloniaProperty.Register<FilterPanel, string?>(nameof(FilterKey));
    public static readonly StyledProperty<ICommand?> AllCommandProperty =
        AvaloniaProperty.Register<FilterPanel, ICommand?>(nameof(AllCommand));
    public static readonly StyledProperty<ICommand?> NoneCommandProperty =
        AvaloniaProperty.Register<FilterPanel, ICommand?>(nameof(NoneCommand));

    public string? Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string? FilterKey { get => GetValue(FilterKeyProperty); set => SetValue(FilterKeyProperty, value); }
    public ICommand? AllCommand { get => GetValue(AllCommandProperty); set => SetValue(AllCommandProperty, value); }
    public ICommand? NoneCommand { get => GetValue(NoneCommandProperty); set => SetValue(NoneCommandProperty, value); }
}
