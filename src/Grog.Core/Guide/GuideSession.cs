// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Guide;

/// <summary>
/// The cursor over <see cref="FirstRunTour.Nodes"/>. This is the single source of truth for where the user
/// is in the tour, and Back/Next are symmetric because both do the same thing: move the cursor.
///
/// <para>TWO positions, deliberately. <c>_reached</c> is how far the tour has got, advanced by app state as
/// the user completes stops. <c>_at</c> is which stop the user is LOOKING at. They are equal while the user
/// follows along; Back separates them, and the tour keeps progressing underneath without yanking the view.
/// The previous design recomputed a single position from state on every render, which is why forward worked
/// and backward could not: there was nothing to recompute backwards to.</para>
///
/// <para>State never moves the cursor backwards. It only advances <c>_reached</c> past stops whose work is
/// already done, which is what lets a user who connects an account before being asked simply skip that stop
/// instead of being dragged back to it.</para>
/// </summary>
public sealed class GuideSession
{
    private readonly HashSet<string> _acked = new();
    private int _at;
    private int _reached;

    public IReadOnlySet<string> Acked => _acked;

    /// <summary>Where the user is looking. Persisted so a restart resumes the same stop.</summary>
    public int Position => _at;

    /// <summary>True when the user has stepped back and is behind the tour's progress.</summary>
    public bool IsBehind => _at < _reached;

    public bool IsAcked(string key) => _acked.Contains(key);

    /// <summary>Back exists only when a stop worth revisiting lies behind the cursor: pass-over stops
    /// (SkipOnBack) do not count, so the sign-in card offers no Back onto its own rail click.</summary>
    public bool CanBack
    {
        get
        {
            for (int i = _at - 1; i >= 0; i--)
                if (!FirstRunTour.Nodes[i].SkipOnBack) return true;
            return false;
        }
    }

    /// <summary>Next exists on every stop except the last. On the live edge it means "I am done with this
    /// one"; behind the edge it simply retraces forward.</summary>
    public bool CanNext => _at < FirstRunTour.Nodes.Count - 1;

    /// <summary>Is the tour finished? The last stop is the sign-off, so completion means acking it.</summary>
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

    /// <summary>A stop is finished when the user acked it (Next) or when its own condition says the work
    /// already happened. Ack first: an explicit Next must stick even if the condition never becomes true.</summary>
    private bool Done(int i, GuideState s)
    {
        var n = FirstRunTour.Nodes[i];
        // An irrelevant stop counts as done: the cursor advances past it without it ever being shown.
        if (n.Relevant?.Invoke(s) == false) return true;
        if (_acked.Contains(n.Key)) return true;
        return n.IsDone?.Invoke(s) == true;
    }

    /// <summary>THE one rule for whether the cursor may rest on a stop: it must be relevant, and a pass-over
    /// stop (a rail click) is never shown going backwards nor once it is already satisfied going forwards.
    /// Back, Next-behind and Sync-behind all ask this; three inlined copies of it had drifted apart (09-02).</summary>
    private bool Showable(int i, GuideState s, bool backwards)
    {
        var n = FirstRunTour.Nodes[i];
        if (n.Relevant?.Invoke(s) == false) return false;
        if (!n.SkipOnBack) return true;
        return !backwards && !Done(i, s);
    }

    /// <summary>Exposed for the invariant tests: how far the tour has got.</summary>
    public int Reached => _reached;

    /// <summary>Let app state advance the tour. Called whenever the state changes. Only ever moves FORWARD,
    /// and only moves what the user is looking at when they were already at the live edge -- so a user
    /// reading an earlier stop is never yanked away by something finishing in the background.</summary>
    /// <returns>True when the cursor moved, so the caller reconciles the page (see <see cref="Landing"/>).</returns>
    public bool Sync(GuideState s)
    {
        int before = _at;
        bool wasAtEdge = _at >= _reached;
        while (_reached < FirstRunTour.Nodes.Count - 1 && Done(_reached, s)) _reached++;
        if (wasAtEdge) _at = _reached;
        // Retracing: a stop that can no longer be shown (a satisfied rail click, an irrelevant stop) is stepped
        // through to the next one that can; real stops still wait for Next.
        else while (_at < _reached && !Showable(_at, s, backwards: false)) _at++;
        return _at != before;
    }

    /// <summary>The stop to draw, with its face chosen: the "open this page" face when the user is elsewhere,
    /// the busy face while the stop's action is running, the landed face once its payoff has arrived.</summary>
    public GuideView Current(GuideState s)
    {
        var n = FirstRunTour.Nodes[_at];

        // One face per stop, chosen only by what is HAPPENING, never by which page the user is on: moving
        // the cursor onto a stop navigates to its page, so the caption and the page cannot disagree.
        var caption = n.Caption;
        var target = n.TargetName;
        if (n.IsBusy?.Invoke(s) == true && !string.IsNullOrEmpty(n.BusyCaption))
        {
            caption = n.BusyCaption;
        }
        else if (n.IsLanded?.Invoke(s) == true)
        {
            // Caption AND target move together: finishing can remove the control the stop was pointing at.
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

    /// <summary>What the App must do after the cursor moves. Returned as data so the session stays pure.</summary>
    /// <param name="NavigateTo">The page the stop now under the cursor lives on, when it is not the page on
    /// screen. Both directions return this: it is why Back restores the right page instead of leaving a past
    /// caption over the present one, and why Next on a "open this page" card actually opens it.</param>
    /// <param name="ScaffoldFolder">The folder stop's Next IS the confirmation of the default folder.</param>
    public readonly record struct Move(string? NavigateTo, bool ScaffoldFolder);

    public Move Back(GuideState s)
    {
        // Pass over transition stops (rail clicks, the modal-bound summary) and irrelevant ones (the backup
        // stops on an empty library): Back lands on the previous stop where something real is on screen.
        // Guarded so an all-skippable prefix still stops at index 0 rather than looping.
        while (_at > 0)
        {
            _at--;
            if (Showable(_at, s, backwards: true)) break;
        }
        return MoveFor(s, scaffold: false);
    }

    /// <summary>Next. Behind the edge it retraces; on the edge it acknowledges this stop and moves on.
    /// A required stop that is not yet done cannot be skipped -- the caller gates on <c>CanSkip</c>.</summary>
    public Move Next(GuideState s)
    {
        bool scaffold = false;
        if (_at < _reached)
        {
            // Retrace forward, passing over irrelevant stops the same way Back does.
            while (_at < _reached)
            {
                _at++;
                if (Showable(_at, s, backwards: false)) break;
            }
        }
        else
        {
            var n = FirstRunTour.Nodes[_at];
            // Next on the folder stop IS the confirmation of the default, so the category folders get made.
            scaffold = n.Key == "folder-default";
            _acked.Add(n.Key);
            Sync(s);
            // Sync only advances past stops it considers done; acking this one guarantees that, but a stop
            // whose successor is ALSO already done should not need a second press.
            if (_at < FirstRunTour.Nodes.Count - 1 && _at <= _reached && Done(_at, s))
            {
                _at = _reached = System.Math.Min(_at + 1, FirstRunTour.Nodes.Count - 1);
            }
        }
        return MoveFor(s, scaffold);
    }

    /// <summary>May Next leave this stop? Optional stops always may; a required one only once its work is
    /// done. Retracing (behind the edge) is always allowed, because those stops are already behind us.</summary>
    public bool CanSkip(GuideState s)
    {
        if (_at < _reached) return true;
        var n = FirstRunTour.Nodes[_at];
        return n.Optional || Done(_at, s);
    }

    /// <summary>The page the stop under the cursor lives on, as a Move, for a caller that moved the cursor by
    /// some path other than Back/Next (a resume, a Sync that stepped). ONE formula for "which page": the same
    /// one Back and Next use, so no transition can leave the cursor on a stop whose page is not on screen.</summary>
    public Move Landing(GuideState s) => MoveFor(s, scaffold: false);

    private Move MoveFor(GuideState s, bool scaffold)
    {
        var n = FirstRunTour.Nodes[_at];
        var want = n.DestinationFor?.Invoke(s) ?? n.RequiresView;
        var dest = string.IsNullOrEmpty(want) || want == s.CurrentView ? null : want;
        return new Move(dest, scaffold);
    }

    /// <summary>Jump the cursor to a stop by key, for a re-run that should land somewhere specific.</summary>
    public void GoTo(string key)
    {
        var i = FirstRunTour.IndexOf(key);
        if (i < 0) return;
        _at = i;
        if (_reached < i) _reached = i;
    }
}
