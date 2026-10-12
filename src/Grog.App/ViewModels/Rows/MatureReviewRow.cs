// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace Grog.App.ViewModels;

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
