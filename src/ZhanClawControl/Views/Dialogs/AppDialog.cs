using System.Windows;
using Microsoft.Win32;

namespace ZhanClawControl.Views.Dialogs;

public enum AppDialogActionStyle
{
    Secondary,
    Primary,
    Danger
}

/// <summary>A stable result id plus a live-localized action label.</summary>
public sealed record AppDialogAction(
    string Id,
    string LabelResourceKey,
    AppDialogActionStyle Style = AppDialogActionStyle.Secondary,
    bool IsDefault = false,
    bool IsCancel = false);

/// <summary>
/// Themed, localized replacement for <see cref="MessageBox"/>. Direct-string
/// overloads ease migration; resource-key overloads keep title and message live
/// when the application language changes while the dialog is open.
/// </summary>
public static class AppDialog
{
    public static MessageBoxResult Show(
        string message,
        string? title = null,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        Window? owner = null,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        ShowCore(
            () => string.IsNullOrWhiteSpace(title) ? App.Localization.Text("ProductName") : title,
            () => message ?? string.Empty,
            buttons,
            image,
            owner,
            defaultResult);

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image) =>
        Show(message, title, buttons, image, owner: null, defaultResult: MessageBoxResult.None);

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons) =>
        Show(message, title, buttons, MessageBoxImage.None, owner: null, defaultResult: MessageBoxResult.None);

    public static MessageBoxResult Show(
        Window owner,
        string message,
        string? title = null,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(message, title, buttons, image, owner, defaultResult);

    public static MessageBoxResult ShowResource(
        string messageResourceKey,
        string titleResourceKey = "ProductName",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        Window? owner = null,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        ShowCore(
            () => App.Localization.Text(titleResourceKey),
            () => App.Localization.Text(messageResourceKey),
            buttons,
            image,
            owner,
            defaultResult);

    public static MessageBoxResult ShowResourceFormat(
        string messageResourceKey,
        object?[] messageArguments,
        string titleResourceKey = "ProductName",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        Window? owner = null,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        ArgumentNullException.ThrowIfNull(messageArguments);
        var arguments = messageArguments.ToArray();
        return ShowCore(
            () => App.Localization.Text(titleResourceKey),
            () => App.Localization.Format(messageResourceKey, arguments),
            buttons,
            image,
            owner,
            defaultResult);
    }

    public static void ShowInformation(string message, string? title = null, Window? owner = null) =>
        Show(message, title ?? App.Localization.Text("DialogInformationTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information, owner);

    public static void ShowWarning(string message, string? title = null, Window? owner = null) =>
        Show(message, title ?? App.Localization.Text("DialogWarningTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning, owner);

    public static void ShowError(string message, string? title = null, Window? owner = null) =>
        Show(message, title ?? App.Localization.Text("DialogErrorTitle"),
            MessageBoxButton.OK, MessageBoxImage.Error, owner);

    public static bool Confirm(
        string message,
        string? title = null,
        Window? owner = null,
        MessageBoxResult defaultResult = MessageBoxResult.Cancel) =>
        Show(message, title ?? App.Localization.Text("DialogConfirmTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question, owner, defaultResult) == MessageBoxResult.OK;

    public static bool AskYesNo(
        string message,
        string? title = null,
        Window? owner = null,
        MessageBoxResult defaultResult = MessageBoxResult.No) =>
        Show(message, title ?? App.Localization.Text("DialogConfirmTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, owner, defaultResult) == MessageBoxResult.Yes;

    /// <summary>
    /// Shows two or three product-specific actions. Exactly one action must be the
    /// Escape/cancel result and exactly one must be the Enter/default result.
    /// </summary>
    public static string ShowActions(
        string messageResourceKey,
        string titleResourceKey,
        IReadOnlyList<AppDialogAction> actions,
        MessageBoxImage image = MessageBoxImage.Question,
        Window? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleResourceKey);
        ValidateActions(actions);
        return ShowActionsCore(
            () => App.Localization.Text(titleResourceKey),
            () => App.Localization.Text(messageResourceKey),
            actions,
            image,
            owner);
    }

    public static string ShowActionsText(
        string message,
        string? title,
        IReadOnlyList<AppDialogAction> actions,
        MessageBoxImage image = MessageBoxImage.Question,
        Window? owner = null)
    {
        ValidateActions(actions);
        return ShowActionsCore(
            () => string.IsNullOrWhiteSpace(title) ? App.Localization.Text("ProductName") : title,
            () => message ?? string.Empty,
            actions,
            image,
            owner);
    }

    public static string ShowActionsFormat(
        string messageResourceKey,
        object?[] messageArguments,
        string titleResourceKey,
        IReadOnlyList<AppDialogAction> actions,
        MessageBoxImage image = MessageBoxImage.Question,
        Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(messageArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleResourceKey);
        ValidateActions(actions);
        var arguments = messageArguments.ToArray();
        return ShowActionsCore(
            () => App.Localization.Text(titleResourceKey),
            () => App.Localization.Format(messageResourceKey, arguments),
            actions,
            image,
            owner);
    }

    /// <summary>Shows a native Windows Open/Save dialog owned by the active app window.</summary>
    public static bool? ShowFileDialog(CommonDialog dialog, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var application = Application.Current;
        if (application is null)
        {
            return dialog.ShowDialog();
        }

        if (!application.Dispatcher.CheckAccess())
        {
            return application.Dispatcher.Invoke(() => ShowFileDialog(dialog, owner));
        }

        var effectiveOwner = ResolveOwner(application, owner);
        try
        {
            return effectiveOwner is null ? dialog.ShowDialog() : dialog.ShowDialog(effectiveOwner);
        }
        catch (InvalidOperationException)
        {
            return dialog.ShowDialog();
        }
    }

    private static MessageBoxResult ShowCore(
        Func<string> titleProvider,
        Func<string> messageProvider,
        MessageBoxButton buttons,
        MessageBoxImage image,
        Window? owner,
        MessageBoxResult defaultResult)
    {
        try
        {
            var application = Application.Current;
            if (application is null)
            {
                return ShowNativeFallback(
                    SafeText(titleProvider, "Zhan Claw"),
                    SafeText(messageProvider, string.Empty),
                    buttons,
                    image,
                    owner,
                    defaultResult);
            }

            if (!application.Dispatcher.CheckAccess())
            {
                return application.Dispatcher.Invoke(() =>
                    ShowCore(titleProvider, messageProvider, buttons, image, owner, defaultResult));
            }

            var effectiveOwner = ResolveOwner(application, owner);
            var dialog = new AppDialogWindow(
                titleProvider,
                messageProvider,
                buttons,
                image,
                defaultResult);
            return dialog.ShowModal(effectiveOwner);
        }
        catch
        {
            // Dialogs are also used in startup and fatal-error paths. If WPF
            // resources, an owner handle, or custom rendering fails, preserve the
            // user's ability to read and answer the prompt with the native dialog.
            return ShowNativeFallback(
                SafeText(titleProvider, "Zhan Claw"),
                SafeText(messageProvider, string.Empty),
                buttons,
                image,
                owner,
                defaultResult);
        }
    }

    private static string ShowActionsCore(
        Func<string> titleProvider,
        Func<string> messageProvider,
        IReadOnlyList<AppDialogAction> actions,
        MessageBoxImage image,
        Window? owner)
    {
        var cancel = actions.Single(action => action.IsCancel);
        try
        {
            var application = Application.Current;
            if (application is null)
            {
                ShowNativeActionsFallback(
                    SafeText(titleProvider, "Zhan Claw"),
                    SafeText(messageProvider, string.Empty),
                    image,
                    owner);
                return cancel.Id;
            }
            if (!application.Dispatcher.CheckAccess())
            {
                return application.Dispatcher.Invoke(() =>
                    ShowActionsCore(titleProvider, messageProvider, actions, image, owner));
            }

            var effectiveOwner = ResolveOwner(application, owner);
            var dialog = new AppDialogWindow(
                titleProvider,
                messageProvider,
                image,
                actions,
                cancel.Id);
            dialog.ShowModal(effectiveOwner);
            return dialog.ActionResult ?? cancel.Id;
        }
        catch
        {
            // Native MessageBox cannot retain arbitrary labels. Fail safe to the
            // explicit cancel action rather than silently selecting a destructive one.
            ShowNativeActionsFallback(
                SafeText(titleProvider, "Zhan Claw"),
                SafeText(messageProvider, string.Empty),
                image,
                owner);
            return cancel.Id;
        }
    }

    private static string SafeText(Func<string> provider, string fallback)
    {
        try
        {
            return provider() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void ShowNativeActionsFallback(
        string title,
        string message,
        MessageBoxImage image,
        Window? requestedOwner)
    {
        try
        {
            var cancelledMessage = $"{message}{Environment.NewLine}{Environment.NewLine}" +
                                   SafeLocalizationText(
                                       "DialogCustomUiFallbackCancelled",
                                       "The requested action was cancelled because the application dialog could not be displayed.");
            var owner = requestedOwner;
            var application = Application.Current;
            if (application is not null && application.Dispatcher.CheckAccess())
            {
                owner = ResolveOwner(application, requestedOwner);
            }

            if (IsUsableOwner(owner))
            {
                MessageBox.Show(owner!, cancelledMessage, title, MessageBoxButton.OK, image);
            }
            else
            {
                MessageBox.Show(cancelledMessage, title, MessageBoxButton.OK, image);
            }
        }
        catch
        {
            // A custom action dialog must fail closed; its caller receives the
            // explicit cancel id even when no UI can be displayed.
        }
    }

    private static string SafeLocalizationText(string resourceKey, string fallback)
    {
        try
        {
            var text = App.Localization.Text(resourceKey);
            return string.IsNullOrWhiteSpace(text) || text == resourceKey ? fallback : text;
        }
        catch
        {
            return fallback;
        }
    }

    private static void ValidateActions(IReadOnlyList<AppDialogAction>? actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count is < 2 or > 3)
            throw new ArgumentException("A dialog requires two or three actions.", nameof(actions));
        if (actions.Any(action => string.IsNullOrWhiteSpace(action.Id) ||
                                  string.IsNullOrWhiteSpace(action.LabelResourceKey)))
            throw new ArgumentException("Every action requires an id and label resource key.", nameof(actions));
        if (actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != actions.Count)
            throw new ArgumentException("Action ids must be unique.", nameof(actions));
        if (actions.Count(action => action.IsDefault) != 1)
            throw new ArgumentException("Exactly one action must handle Enter/default.", nameof(actions));
        if (actions.Count(action => action.IsCancel) != 1)
            throw new ArgumentException("Exactly one action must handle Escape/cancel.", nameof(actions));
        if (actions.Any(action => action.Style == AppDialogActionStyle.Danger && action.IsDefault))
            throw new ArgumentException("A destructive action cannot be the Enter/default action.", nameof(actions));
    }

    private static Window? ResolveOwner(Application application, Window? requestedOwner)
    {
        if (IsUsableOwner(requestedOwner))
        {
            return requestedOwner;
        }

        var active = application.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive && IsUsableOwner(window));
        if (active is not null)
        {
            return active;
        }

        if (IsUsableOwner(application.MainWindow))
        {
            return application.MainWindow;
        }

        // The first-run wizard can be visible before Application.MainWindow is
        // assigned, and an async completion prompt can arrive after it loses
        // activation. Keep that prompt modal to the visible product window.
        return application.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window is not AppDialogWindow && IsUsableOwner(window));
    }

    private static bool IsUsableOwner(Window? window) =>
        window is not null && window.IsVisible && window.Dispatcher.CheckAccess();

    private static MessageBoxResult ShowNativeFallback(
        string title,
        string message,
        MessageBoxButton buttons,
        MessageBoxImage image,
        Window? requestedOwner,
        MessageBoxResult defaultResult)
    {
        var normalizedDefault = NormalizeNativeDefault(buttons, defaultResult);
        try
        {
            var owner = requestedOwner;
            var application = Application.Current;
            if (application is not null && application.Dispatcher.CheckAccess())
            {
                owner = ResolveOwner(application, requestedOwner);
            }

            return IsUsableOwner(owner)
                ? MessageBox.Show(owner!, message, title, buttons, image, normalizedDefault)
                : MessageBox.Show(message, title, buttons, image, normalizedDefault);
        }
        catch
        {
            return buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.None
            };
        }
    }

    private static MessageBoxResult NormalizeNativeDefault(
        MessageBoxButton buttons,
        MessageBoxResult requested) => buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel when requested == MessageBoxResult.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo when requested == MessageBoxResult.Yes => MessageBoxResult.Yes,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel when requested is MessageBoxResult.Yes or MessageBoxResult.No => requested,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.None
        };
}
