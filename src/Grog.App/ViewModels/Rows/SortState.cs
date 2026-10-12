// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace Grog.App.ViewModels;

public sealed class SortState<TKey> where TKey : struct, Enum
{
    private readonly Func<TKey, bool> _defaultAsc;

    /// <param name="defaultAsc">Direction a key starts in when first selected (e.g. Name A-Z ascending,
    /// Size biggest-first descending). Null = everything starts ascending.</param>
    public SortState(TKey initial, Func<TKey, bool>? defaultAsc = null, bool asc = true)
    { Key = initial; _defaultAsc = defaultAsc ?? (_ => true); Asc = asc; }

    public TKey Key { get; private set; }
    public bool Asc { get; private set; }

    public void Toggle(TKey key)
    {
        if (EqualityComparer<TKey>.Default.Equals(Key, key)) Asc = !Asc;
        else { Key = key; Asc = _defaultAsc(key); }
    }

    public void Reset(TKey key, bool asc) { Key = key; Asc = asc; }

    public bool Is(TKey key) => EqualityComparer<TKey>.Default.Equals(Key, key);

    public string Glyph(TKey key) => Is(key) ? (Asc ? "  ▲" : "  ▼") : "";
}
