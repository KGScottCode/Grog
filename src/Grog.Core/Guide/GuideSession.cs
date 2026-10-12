// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Guide;

public sealed class GuideSession
{
    private readonly HashSet<string> _acked = new();
    private int _at;
    private int _reached;

    public IReadOnlySet<string> Acked => _acked;

    public int Position => _at;

    public bool IsBehind => _at < _reached;

    public bool IsAcked(string key) => _acked.Contains(key);

    public bool CanBack
    {
        get
        {
            for (int i = _at - 1; i >= 0; i--)
                if (!FirstRunTour.Nodes[i].SkipOnBack) return true;
            return false;
        }
    }

    public bool CanNext => _at < FirstRunTour.Nodes.Count - 1;

    public bool IsComplete => _acked.Contains(FirstRunTour.Nodes[^1].Key);

    public void Load(IEnumerable<string> acked, int position)
    {
        _acked.Clear();
        foreach (var k in acked) _acked.Add(k);
        _at = _reached = Clamp(position);
    }

    public void Reset()
    {
        _acked.Clear();
        _at = _reached = 0;
    }

    private static int Clamp(int i) => i < 0 ? 0 : i >= FirstRunTour.Nodes.Count ? FirstRunTour.Nodes.Count - 1 : i;

    private bool Done(int i, GuideState s)
    {
        var n = FirstRunTour.Nodes[i];
        // An irrelevant stop counts as done
        if (n.Relevant?.Invoke(s) == false) return true;
        if (_acked.Contains(n.Key)) return true;
        return n.IsDone?.Invoke(s) == true;
    }

    private bool Showable(int i, GuideState s, bool backwards)
    {
        var n = FirstRunTour.Nodes[i];
        if (n.Relevant?.Invoke(s) == false) return false;
        if (!n.SkipOnBack) return true;
        return !backwards && !Done(i, s);
    }

    public int Reached => _reached;

    public bool Sync(GuideState s)
    {
        int before = _at;
        bool wasAtEdge = _at >= _reached;
        while (_reached < FirstRunTour.Nodes.Count - 1 && Done(_reached, s)) _reached++;
        if (wasAtEdge) _at = _reached;
        // Retracing: a stop that can no longer be shown
        else while (_at < _reached && !Showable(_at, s, backwards: false)) _at++;
        return _at != before;
    }

    public GuideView Current(GuideState s)
    {
        var n = FirstRunTour.Nodes[_at];
        var caption = n.Caption;
        var target = n.TargetName;
        if (n.IsBusy?.Invoke(s) == true && !string.IsNullOrEmpty(n.BusyCaption))
        {
            caption = n.BusyCaption;
        }
        else if (n.IsLanded?.Invoke(s) == true)
        {
            // Caption AND target move together
            if (!string.IsNullOrEmpty(n.DoneCaption)) caption = n.DoneCaption;
            if (!string.IsNullOrEmpty(n.DoneTarget)) target = n.DoneTarget;
        }
        else if (n.IsAlt?.Invoke(s) == true)
        {
            if (!string.IsNullOrEmpty(n.AltCaption)) caption = n.AltCaption;
            if (!string.IsNullOrEmpty(n.AltTarget)) target = n.AltTarget;
        }

        bool onPage = string.IsNullOrEmpty(n.RequiresView) || n.RequiresView == s.CurrentView;
        return new GuideView(n.Key, n.Title, caption, target, n.RequiresView, onPage,
                             n.Detail, n.DetailTitle, n.Optional, n.Gate, n.ScopeName, n.AnchorName,
                             n.NoScrim, n.Below, n.Park);
    }

    public readonly record struct Move(string? NavigateTo, bool ScaffoldFolder);

    public Move Back(GuideState s)
    {
        while (_at > 0)
        {
            _at--;
            if (Showable(_at, s, backwards: true)) break;
        }
        return MoveFor(s, scaffold: false);
    }

    public Move Next(GuideState s)
    {
        bool scaffold = false;
        if (_at < _reached)
        {
            while (_at < _reached)
            {
                _at++;
                if (Showable(_at, s, backwards: false)) break;
            }
        }
        else
        {
            var n = FirstRunTour.Nodes[_at];
            scaffold = n.Key == "folder-default";
            _acked.Add(n.Key);
            Sync(s);

            if (_at < FirstRunTour.Nodes.Count - 1 && _at <= _reached && Done(_at, s))
            {
                _at = _reached = System.Math.Min(_at + 1, FirstRunTour.Nodes.Count - 1);
            }
        }
        return MoveFor(s, scaffold);
    }

    public bool CanSkip(GuideState s)
    {
        if (_at < _reached) return true;
        var n = FirstRunTour.Nodes[_at];
        return n.Optional || Done(_at, s);
    }

    public Move Landing(GuideState s) => MoveFor(s, scaffold: false);

    private Move MoveFor(GuideState s, bool scaffold)
    {
        var n = FirstRunTour.Nodes[_at];
        var want = n.DestinationFor?.Invoke(s) ?? n.RequiresView;
        var dest = string.IsNullOrEmpty(want) || want == s.CurrentView ? null : want;
        return new Move(dest, scaffold);
    }

    public void GoTo(string key)
    {
        var i = FirstRunTour.IndexOf(key);
        if (i < 0) return;
        _at = i;
        if (_reached < i) _reached = i;
    }
}
