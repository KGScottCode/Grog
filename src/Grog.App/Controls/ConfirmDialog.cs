// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Grog.App.Controls;

/// <summary>
/// THE modal: scrim, centered card, title, body, an optional extra slot (a checkbox, a typed gate) and a
/// Cancel / Confirm pair. Before 09-02 MainWindow.axaml carried 30 hand-built copies of this shape with
/// drifting padding (30,26 / 34,28 / 36,30), spacing, widths and default buttons. The view-model contract
/// is untouched: each dialog still binds its own Show flag, texts and commands; only the chrome is shared.
/// <para><see cref="Danger"/> flips the confirm button to the destructive red tier and
/// makes CANCEL the Enter default (a destructive verb is never one keystroke away).</para>
/// </summary>
public sealed class ConfirmDialog : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Title));
    public static readonly StyledProperty<string?> BodyProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Body));
    /// <summary>A quieter second line under the body ("Nothing on disk is deleted."). When present the body
    /// is the statement (white) and the note the reassurance (dim).</summary>
    public static readonly StyledProperty<string?> NoteProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Note));
    public static readonly StyledProperty<string?> CancelTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(CancelText), "Cancel");
    public static readonly StyledProperty<string?> ConfirmTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(ConfirmText), "OK");
    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ConfirmDialog, ICommand?>(nameof(CancelCommand));
    /// <summary>An optional middle choice ("Continue Without Moving", "Skip Scan &amp; Add"): secondary tier,
    /// or the red-ink ghost when <see cref="AltDanger"/>. Hidden while AltText is empty.</summary>
    public static readonly StyledProperty<string?> AltTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(AltText));
    public static readonly StyledProperty<ICommand?> AltCommandProperty =
        AvaloniaProperty.Register<ConfirmDialog, ICommand?>(nameof(AltCommand));
    public static readonly StyledProperty<bool> AltDangerProperty =
        AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(AltDanger));
    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<ConfirmDialog, ICommand?>(nameof(ConfirmCommand));
    public static readonly StyledProperty<bool> DangerProperty =
        AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(Danger));
    /// <summary>Red title and stroke: the dialog itself is the warning (files about to be flagged missing),
    /// not just its verb. Rare on purpose; red on the card is louder than red on the button.</summary>
    public static readonly StyledProperty<bool> AlarmProperty =
        AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(Alarm));
    /// <summary>Hide the Cancel button for a single-acknowledgement notice.</summary>
    public static readonly StyledProperty<bool> ShowCancelProperty =
        AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(ShowCancel), true);
    /// <summary>False disables Confirm: the typed-word gates bind this to their match flag.</summary>
    public static readonly StyledProperty<bool> CanConfirmProperty =
        AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(CanConfirm), true);
    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<ConfirmDialog, double>(nameof(CardWidth), 500);

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public string? Note { get => GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    public string? CancelText { get => GetValue(CancelTextProperty); set => SetValue(CancelTextProperty, value); }
    public string? ConfirmText { get => GetValue(ConfirmTextProperty); set => SetValue(ConfirmTextProperty, value); }
    public ICommand? CancelCommand { get => GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
    public string? AltText { get => GetValue(AltTextProperty); set => SetValue(AltTextProperty, value); }
    public ICommand? AltCommand { get => GetValue(AltCommandProperty); set => SetValue(AltCommandProperty, value); }
    public bool AltDanger { get => GetValue(AltDangerProperty); set => SetValue(AltDangerProperty, value); }
    public ICommand? ConfirmCommand { get => GetValue(ConfirmCommandProperty); set => SetValue(ConfirmCommandProperty, value); }
    public bool Danger { get => GetValue(DangerProperty); set => SetValue(DangerProperty, value); }
    public bool Alarm { get => GetValue(AlarmProperty); set => SetValue(AlarmProperty, value); }
    public bool ShowCancel { get => GetValue(ShowCancelProperty); set => SetValue(ShowCancelProperty, value); }
    public bool CanConfirm { get => GetValue(CanConfirmProperty); set => SetValue(CanConfirmProperty, value); }
    public double CardWidth { get => GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }

    static ConfirmDialog()
    {
        // The scrim sits above every page; the dialog is a question the user must answer.
        ZIndexProperty.OverrideDefaultValue<ConfirmDialog>(100);
        DangerProperty.Changed.AddClassHandler<ConfirmDialog>((d, _) => d.PseudoClasses.Set(":danger", d.Danger));
        AlarmProperty.Changed.AddClassHandler<ConfirmDialog>((d, _) => d.PseudoClasses.Set(":alarm", d.Alarm));
        NoteProperty.Changed.AddClassHandler<ConfirmDialog>((d, _) => d.PseudoClasses.Set(":noted", !string.IsNullOrEmpty(d.Note)));
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        PseudoClasses.Set(":danger", Danger);
        PseudoClasses.Set(":alarm", Alarm);
        PseudoClasses.Set(":noted", !string.IsNullOrEmpty(Note));
    }
}
