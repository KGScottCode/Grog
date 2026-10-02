// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace Grog.App.ViewModels;

/// <summary>(09-26, owner) One game in the mature review: its ratings and the user's Keep choice.
/// <see cref="GogOnly"/> = rated by GOG's own store rating and no retail board (ESRB/PEGI/USK/BR).</summary>
public sealed partial class MatureReviewRow : ObservableObject
{
    public MatureReviewRow(long gogId, string title, string ratings, bool gogOnly)
    { GogId = gogId; Title = title; Ratings = ratings; GogOnly = gogOnly; }
    public long GogId { get; }
    public string Title { get; }
    public string Ratings { get; }
    public bool GogOnly { get; }
    [ObservableProperty] private bool _keep;
}
