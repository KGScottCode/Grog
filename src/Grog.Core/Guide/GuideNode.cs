// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Guide;

/// <summary>
/// One stop on the tour. The list of these IS the tour, in order, and the cursor into that list is the
/// single source of truth for where the user is. Back and Next move the cursor; app state only ever
/// ADVANCES it past stops whose work is already done. Nothing recomputes position from scratch.
///
/// <para>This replaces a model where the step was derived from a snapshot on every render. Forward worked
/// (doing things flipped the guards in order) but backward could not: there was nothing to derive
/// backwards to, so a separate trail replayed old captions over a live app, and the two directions
/// disagreed about the page, the buttons and the spotlight.</para>
///
/// <para>Each rail click is its own stop, ahead of the page stop it leads to. They share a
/// <see cref="Number"/>, so the counter spans both halves of one piece of work rather than counting clicks.</para>
/// </summary>
/// <param name="Key">Stable id: tests, persistence and the renderer all use it.</param>
/// <param name="Title">Heading shown above the caption.</param>
/// <param name="Number">The numbered milestone this stop belongs to, or 0 for stops that report rather
/// than ask (the confirmation and the sign-off), which carry a title and no number.</param>
/// <param name="Caption">What to do here, or what is happening.</param>
/// <param name="TargetName">x:Name of the control to spotlight.</param>
/// <param name="RequiresView">The page this stop lives on. Moving the cursor onto it navigates there, which
/// is what keeps Back honest: the caption and the page can never disagree. Null = works anywhere.</param>
/// <param name="BusyCaption">Face shown while <see cref="IsBusy"/> holds, for a stop whose control changes
/// meaning mid-action (the scan button becomes Cancel).</param>
/// <param name="DoneCaption">Face shown once <see cref="IsLanded"/> holds: the payoff arrived while the user
/// was watching.</param>
/// <param name="Optional">Nothing here MUST happen, so Next may leave it.</param>
/// <param name="Gate">Nothing past this stop makes sense yet; Next is hidden rather than shown disabled.</param>
/// <param name="SkipOnBack">Back passes over this stop. Rail clicks are pure transitions (revisiting "open
/// this page" while standing on it says nothing), and the scan summary's target lives inside a modal that
/// is no longer on screen. Forward behavior is unchanged.</param>
public readonly record struct GuideNode(
    string Key,
    string Title,
    int Number,
    string Caption,
    string TargetName,
    string? RequiresView = null,
    string BusyCaption = "",
    string DoneCaption = "",
    // The control to ring once IsLanded holds. Needed where the ACT of finishing removes the original
    // target: a downloaded file's arrow is replaced by a tick, and a ring with no control falls back to
    // a corner of the window.
    string DoneTarget = "",
    // An ALTERNATE face for a different situation (not a payoff): the empty-library wording of a stop, or
    // the control to ring when the usual one is not on screen. Chosen by IsAlt; distinct from the landed face
    // so "this stop's payoff arrived" and "this stop reads differently here" are never one hook (09-02).
    string AltCaption = "",
    string AltTarget = "",
    string Detail = "",
    string DetailTitle = "",
    bool Optional = false,
    bool Gate = false,
    bool SkipOnBack = false,
    string ScopeName = "",
    string AnchorName = "",
    bool NoScrim = false,
    bool Below = false,
    bool Park = false)
{
    /// <summary>Is this stop's work already done? Null = only an explicit Next completes it, which is what
    /// Optional stops rely on.</summary>
    public Func<GuideState, bool>? IsDone { get; init; }

    /// <summary>Is the stop mid-action, so it should show <see cref="BusyCaption"/>?</summary>
    public Func<GuideState, bool>? IsBusy { get; init; }

    /// <summary>Has the payoff landed, so it should show <see cref="DoneCaption"/>?</summary>
    public Func<GuideState, bool>? IsLanded { get; init; }

    /// <summary>Is the alternate situation in force, so it should show <see cref="AltCaption"/> / ring
    /// <see cref="AltTarget"/>? Checked after busy and landed.</summary>
    public Func<GuideState, bool>? IsAlt { get; init; }

    /// <summary>Does this stop apply at all, given the state? Null = always. An irrelevant stop is invisible
    /// to the cursor in BOTH directions -- the empty-library case: with nothing scanned in, "back up your
    /// first file" has no file to point at and "queue the rest" queues nothing.</summary>
    public Func<GuideState, bool>? Relevant { get; init; }

    /// <summary>Where arriving on this stop should take the user, decided from state, for a stop whose right
    /// page is not fixed. Overrides <see cref="RequiresView"/> for navigation when set. The sign-off uses it:
    /// a running full backup lands on Overview to watch it; otherwise Library, where the next action is.</summary>
    public Func<GuideState, string?>? DestinationFor { get; init; }
}
