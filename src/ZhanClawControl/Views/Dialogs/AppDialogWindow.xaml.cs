using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using ZhanClawControl.Views;

namespace ZhanClawControl.Views.Dialogs;

public sealed partial class AppDialogWindow : Window
{
    private readonly Func<string> _titleProvider;
    private readonly Func<string> _messageProvider;
    private readonly MessageBoxButton _buttons;
    private readonly MessageBoxImage _image;
    private readonly MessageBoxResult _defaultResult;
    private readonly IReadOnlyList<AppDialogAction>? _actions;
    private readonly string? _escapeActionId;
    private readonly List<(Button Button, string ResourceKey)> _localizedButtons = new();
    private Button? _defaultButton;
    private bool _completed;

    internal AppDialogWindow(
        Func<string> titleProvider,
        Func<string> messageProvider,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
        : this(titleProvider, messageProvider, buttons, image, defaultResult, null, null)
    {
    }

    internal AppDialogWindow(
        Func<string> titleProvider,
        Func<string> messageProvider,
        MessageBoxImage image,
        IReadOnlyList<AppDialogAction> actions,
        string escapeActionId)
        : this(titleProvider, messageProvider, MessageBoxButton.OK, image,
            MessageBoxResult.None, actions, escapeActionId)
    {
    }

    private AppDialogWindow(
        Func<string> titleProvider,
        Func<string> messageProvider,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult,
        IReadOnlyList<AppDialogAction>? actions,
        string? escapeActionId)
    {
        _titleProvider = titleProvider ?? throw new ArgumentNullException(nameof(titleProvider));
        _messageProvider = messageProvider ?? throw new ArgumentNullException(nameof(messageProvider));
        _buttons = buttons;
        _image = image;
        _actions = actions;
        _escapeActionId = escapeActionId;
        _defaultResult = actions is null
            ? NormalizeDefaultResult(buttons, defaultResult)
            : MessageBoxResult.None;

        InitializeComponent();
        ConfigureIcon();
        ConfigureButtons();
        RefreshLocalizedContent();

        App.Theme.Track(this);
        App.Localization.LanguageChanged += OnLanguageChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    internal MessageBoxResult Result { get; private set; } = MessageBoxResult.None;
    internal string? ActionResult { get; private set; }

    internal MessageBoxResult ShowModal(Window? owner)
    {
        var workArea = GetWorkAreaInDip(owner);
        MaxWidth = Math.Min(680, Math.Max(MinWidth, workArea.Width * 0.8));
        MaxHeight = Math.Min(720, Math.Max(280, workArea.Height * 0.8));
        MessageText.MaxHeight = Math.Min(360, Math.Max(80, MaxHeight - 190));

        if (owner is not null && owner.IsVisible && owner != this)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }

        ShowDialog();
        return Result;
    }

    private static Size GetWorkAreaInDip(Window? owner)
    {
        try
        {
            var ownerHandle = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var screen = ownerHandle == IntPtr.Zero
                ? WinForms.Screen.PrimaryScreen
                : WinForms.Screen.FromHandle(ownerHandle);
            if (screen is null)
            {
                return SystemParameters.WorkArea.Size;
            }

            var source = ownerHandle == IntPtr.Zero ? null : HwndSource.FromHwnd(ownerHandle);
            var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = fromDevice.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            var bottomRight = fromDevice.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
            return new Size(
                Math.Max(1, bottomRight.X - topLeft.X),
                Math.Max(1, bottomRight.Y - topLeft.Y));
        }
        catch
        {
            return SystemParameters.WorkArea.Size;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _defaultButton?.Focus();
        if (_defaultButton is not null)
        {
            Keyboard.Focus(_defaultButton);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_actions is null)
            {
                Complete(EscapeResult(_buttons));
            }
            else
            {
                CompleteAction(_escapeActionId!);
            }
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void ConfigureIcon()
    {
        var semanticImage = NormalizeImage(_image);
        switch (semanticImage)
        {
            case MessageBoxImage.Error:
                SemanticIcon.Primary = PhosphorIcons.XCircle;
                SemanticIcon.Secondary = PhosphorIcons.XCircleSecondary;
                SetIconBrush("DangerBrush");
                break;
            case MessageBoxImage.Warning:
                SemanticIcon.Primary = PhosphorIcons.Warning;
                SemanticIcon.Secondary = PhosphorIcons.WarningSecondary;
                SetIconBrush("WarningBrush");
                break;
            case MessageBoxImage.Question:
                SemanticIcon.Primary = DialogIcons.Question;
                SemanticIcon.Secondary = DialogIcons.QuestionSecondary;
                SetIconBrush("BrandBrush");
                break;
            case MessageBoxImage.Information:
                SemanticIcon.Primary = DialogIcons.Information;
                SemanticIcon.Secondary = DialogIcons.InformationSecondary;
                SetIconBrush("BrandBrush");
                break;
            default:
                IconContainer.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void SetIconBrush(string resourceKey) =>
        SemanticIcon.SetResourceReference(PhosphorIcon.ForegroundProperty, resourceKey);

    private void ConfigureButtons()
    {
        if (_actions is not null)
        {
            ConfigureActionButtons(_actions);
            return;
        }

        var definitions = _buttons switch
        {
            MessageBoxButton.OK =>
                new[] { new DialogButtonDefinition(MessageBoxResult.OK, "CommonOk", true) },
            MessageBoxButton.OKCancel =>
                new[]
                {
                    new DialogButtonDefinition(MessageBoxResult.OK, "CommonOk", true),
                    new DialogButtonDefinition(MessageBoxResult.Cancel, "CommonCancel", false)
                },
            MessageBoxButton.YesNo =>
                new[]
                {
                    new DialogButtonDefinition(MessageBoxResult.Yes, "CommonYes", true),
                    new DialogButtonDefinition(MessageBoxResult.No, "CommonNo", false)
                },
            MessageBoxButton.YesNoCancel =>
                new[]
                {
                    new DialogButtonDefinition(MessageBoxResult.Yes, "CommonYes", true),
                    new DialogButtonDefinition(MessageBoxResult.No, "CommonNo", false),
                    new DialogButtonDefinition(MessageBoxResult.Cancel, "CommonCancel", false)
                },
            _ => throw new ArgumentOutOfRangeException(nameof(_buttons), _buttons, "Unsupported dialog button set.")
        };

        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var button = new Button
            {
                MinWidth = 88,
                Margin = index == 0 ? new Thickness(0) : new Thickness(8, 0, 0, 0),
                Tag = definition.Result,
                IsDefault = definition.Result == _defaultResult
            };
            button.SetResourceReference(ContentControl.ContentProperty, definition.ResourceKey);
            if (definition.IsPrimary)
            {
                button.Style = (Style)FindResource("AccentButton");
            }

            button.Click += OnButtonClick;
            ActionPanel.Children.Add(button);
            _localizedButtons.Add((button, definition.ResourceKey));
            if (button.IsDefault)
            {
                _defaultButton = button;
            }
        }
    }

    private void ConfigureActionButtons(IReadOnlyList<AppDialogAction> actions)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var button = new Button
            {
                MinWidth = 88,
                Margin = index == 0 ? new Thickness(0) : new Thickness(8, 0, 0, 0),
                Tag = action.Id,
                IsDefault = action.IsDefault
            };
            button.SetResourceReference(ContentControl.ContentProperty, action.LabelResourceKey);
            switch (action.Style)
            {
                case AppDialogActionStyle.Primary:
                    button.Style = (Style)FindResource("AccentButton");
                    break;
                case AppDialogActionStyle.Danger:
                    button.Style = (Style)FindResource("DangerButton");
                    break;
            }

            button.Click += OnActionButtonClick;
            ActionPanel.Children.Add(button);
            _localizedButtons.Add((button, action.LabelResourceKey));
            if (button.IsDefault)
            {
                _defaultButton = button;
            }
        }
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MessageBoxResult result })
        {
            Complete(result);
        }
    }

    private void OnActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            CompleteAction(id);
        }
    }

    private void Complete(MessageBoxResult result)
    {
        if (_completed)
        {
            return;
        }

        Result = result;
        _completed = true;
        Close();
    }

    private void CompleteAction(string id)
    {
        if (_completed)
        {
            return;
        }

        ActionResult = id;
        _completed = true;
        Close();
    }

    private void RefreshLocalizedContent()
    {
        var requestedTitle = _titleProvider();
        var semanticTitle = SemanticTitle(_image);
        var productName = App.Localization.Text("ProductName");
        Title = requestedTitle;
        HeadingText.Text = string.Equals(requestedTitle, productName, StringComparison.Ordinal)
            ? semanticTitle ?? requestedTitle
            : requestedTitle;
        MessageText.Text = _messageProvider();

        var accessibleTitle = semanticTitle is null ||
                              string.Equals(HeadingText.Text, semanticTitle, StringComparison.Ordinal)
            ? HeadingText.Text
            : $"{HeadingText.Text} — {semanticTitle}";
        AutomationProperties.SetName(this, accessibleTitle);
        AutomationProperties.SetName(HeadingText, HeadingText.Text);
        AutomationProperties.SetName(MessageText, MessageText.Text);
        foreach (var (button, resourceKey) in _localizedButtons)
        {
            AutomationProperties.SetName(button, App.Localization.Text(resourceKey));
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshLocalizedContent();
        }
        else
        {
            Dispatcher.Invoke(RefreshLocalizedContent);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed)
        {
            if (_actions is null)
            {
                Result = EscapeResult(_buttons);
            }
            else
            {
                ActionResult = _escapeActionId;
            }
            _completed = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        App.Localization.LanguageChanged -= OnLanguageChanged;
        foreach (var (button, _) in _localizedButtons)
        {
            button.Click -= OnButtonClick;
            button.Click -= OnActionButtonClick;
        }
    }

    private static MessageBoxResult NormalizeDefaultResult(
        MessageBoxButton buttons,
        MessageBoxResult requested)
    {
        if (IsResultAvailable(buttons, requested))
        {
            return requested;
        }

        return buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Unsupported dialog button set.")
        };
    }

    private static bool IsResultAvailable(MessageBoxButton buttons, MessageBoxResult result) =>
        buttons switch
        {
            MessageBoxButton.OK => result == MessageBoxResult.OK,
            MessageBoxButton.OKCancel => result is MessageBoxResult.OK or MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => result is MessageBoxResult.Yes or MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => result is MessageBoxResult.Yes or MessageBoxResult.No or MessageBoxResult.Cancel,
            _ => false
        };

    private static MessageBoxResult EscapeResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => MessageBoxResult.OK,
        MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        _ => MessageBoxResult.None
    };

    private static MessageBoxImage NormalizeImage(MessageBoxImage image) => (int)image switch
    {
        16 => MessageBoxImage.Error,
        32 => MessageBoxImage.Question,
        48 => MessageBoxImage.Warning,
        64 => MessageBoxImage.Information,
        _ => MessageBoxImage.None
    };

    private static string? SemanticTitle(MessageBoxImage image) => NormalizeImage(image) switch
    {
        MessageBoxImage.Error => App.Localization.Text("DialogErrorTitle"),
        MessageBoxImage.Warning => App.Localization.Text("DialogWarningTitle"),
        MessageBoxImage.Information => App.Localization.Text("DialogInformationTitle"),
        MessageBoxImage.Question => App.Localization.Text("DialogConfirmTitle"),
        _ => null
    };

    private sealed record DialogButtonDefinition(
        MessageBoxResult Result,
        string ResourceKey,
        bool IsPrimary);
}
