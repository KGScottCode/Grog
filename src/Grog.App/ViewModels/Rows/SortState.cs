// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace Grog.App.ViewModels;

/// <summary>One sort scaffold for every sortable list (device grids, cloud saves, the download queue).
/// Three parallel copies of key + direction + toggle + glyph existed, and their glyphs had already
/// drifted by a space; the vocabulary lives here once.</summary>
public sealed class SortState<TKey> where TKey : struct, Enum
{
    private readonly Func<TKey, bool> _defaultAsc;

    /// <param name="defaultAsc">Direction a key starts in when first selected (e.g. Name A-Z ascending,
    /// Size biggest-first descending). Null = everything starts ascending.</param>
    public SortState(TKey initial, Func<TKey, bool>? defaultAsc = null, bool asc = true)
    { Key = initial; _defaultAsc = defaultAsc ?? (_ => true); Asc = asc; }

    public TKey Key { get; private set; }
    public bool Asc { get; private set; }

    /// <summary>Same key flips direction; a new key selects it in its default direction.</summary>
    public void Toggle(TKey key)
    {
        if (EqualityComparer<TKey>.Default.Equals(Key, key)) Asc = !Asc;
        else { Key = key; Asc = _defaultAsc(key); }
    }

    public void Reset(TKey key, bool asc) { Key = key; Asc = asc; }

    public bool Is(TKey key) => EqualityComparer<TKey>.Default.Equals(Key, key);

    /// <summary>THE one glyph rendering: two spaces then the arrow, empty for unselected keys.</summary>
    public string Glyph(TKey key) => Is(key) ? (Asc ? "  ▲" : "  ▼") : "";
}
