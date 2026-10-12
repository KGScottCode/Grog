// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Grog.App.Controls;

public sealed class ConfirmDialog : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Title));
    public static readonly StyledProperty<string?> BodyProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Body));
    public static readonly StyledProperty<string?> NoteProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Note));
    public static readonly StyledProperty<string?> CancelTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(CancelText), "Cancel");
    public static readonly StyledProperty<string?> ConfirmTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(ConfirmText), "OK");
    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ConfirmDialog, ICommand?>(nameof(CancelCommand));
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
    public static readonly StyledProperty<bool> AlarmProperty =
        AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(Alarm));
    public static readonly StyledProperty<bool> ShowCancelProperty =
        AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(ShowCancel), true);
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
