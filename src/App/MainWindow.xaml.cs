using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using SwBridge.Connection;

namespace Nodestar.App;

/// <summary>
/// Hosts the Nodestar companion experience and coordinates the paired
/// SOLIDWORKS launch workflow.
/// </summary>
public partial class MainWindow : Window
{
    private const double DockedWidthRatio = 0.20;
    private const string DefaultSolidWorksExecutablePath =
        @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe";

    private readonly ISwConnector _connector = new SwConnector();
    private bool _isStatusMenuOpen;
    private bool _isSynchronizingSolidWorksWindow;

    /// <summary>
    /// Initializes the window and seeds the preview conversation.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += OnWindowStateChanged;
        SizeChanged += OnWindowSizeChanged;
        LocationChanged += OnWindowLocationChanged;
        Closing += OnClosing;

        _connector.StateChanged += OnConnectorStateChanged;

        SeedConversation();
        UpdateSendButtonState();
        UpdateStatusDisplay(SwConnectionState.Launching);
    }

    /// <summary>
    /// Positions the window as a docked panel on the right side of the primary work area
    /// and begins launching SOLIDWORKS.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DockToRightSideOfScreen();
        UpdateWindowSurfaceMargin();
        UpdateMaximizeRestoreGlyph();
        PromptTextBox.Focus();

        await LaunchSolidWorksAsync();
    }

    /// <summary>
    /// Allows the custom header surface to drag the borderless window.
    /// </summary>
    private void HeaderBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeRestoreButton_OnClick(sender, e);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// Opens the status actions menu.
    /// </summary>
    private void StatusPillButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isStatusMenuOpen || StatusContextMenu is null)
        {
            return;
        }

        CloseSolidWorksMenuItem.IsEnabled = _connector.HasActiveProcess;
        StatusContextMenu.PlacementTarget = StatusPillButton;
        StatusContextMenu.Closed -= StatusContextMenu_OnClosed;
        StatusContextMenu.Closed += StatusContextMenu_OnClosed;
        _isStatusMenuOpen = true;
        StatusContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Tracks the state of the status actions menu.
    /// </summary>
    private void StatusContextMenu_OnClosed(object? sender, RoutedEventArgs e)
    {
        _isStatusMenuOpen = false;
    }

    /// <summary>
    /// Updates the custom shell when the window enters or leaves maximized state.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateWindowSurfaceMargin();
        UpdateMaximizeRestoreGlyph();
        _ = SyncSolidWorksWindowAsync();
    }

    /// <summary>
    /// Keeps the custom shell aligned while the user resizes the window.
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowSurfaceMargin();
        _ = SyncSolidWorksWindowAsync();
    }

    /// <summary>
    /// Keeps the SOLIDWORKS window aligned to the remaining desktop space when Nodestar moves.
    /// </summary>
    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        _ = SyncSolidWorksWindowAsync();
    }

    /// <summary>
    /// Responds to connector state changes on the UI thread.
    /// </summary>
    private async void OnConnectorStateChanged(object? sender, SwConnectionState state)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateStatusDisplay(state);
        });

        if (state == SwConnectionState.Ready)
        {
            await SyncSolidWorksWindowAsync();
        }
        else if (state == SwConnectionState.Lost)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AddAssistantMessage(
                    "system",
                    "The SOLIDWORKS instance exited unexpectedly. Nodestar is still open, but the CAD session is no longer available.");
            });
        }
    }

    /// <summary>
    /// Shows a placeholder settings dialog until the settings surface exists.
    /// </summary>
    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "Settings are not wired yet. This button is in place so the shell matches the intended companion-app layout.",
            "nodestar settings",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Minimizes the companion panel.
    /// </summary>
    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Toggles the window between maximized and restored states.
    /// </summary>
    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    /// <summary>
    /// Closes the active SOLIDWORKS instance without closing Nodestar.
    /// </summary>
    private async void CloseSolidWorksMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_connector.HasActiveProcess)
        {
            return;
        }

        try
        {
            UpdateStatusDisplay(SwConnectionState.Launching);
            AddAssistantMessage("system", "Closing the SOLIDWORKS instance...");
            await _connector.DisconnectAsync();
            AddAssistantMessage("system", "SOLIDWORKS was closed. Nodestar remains available.");
        }
        catch (Exception ex)
        {
            UpdateStatusDisplay(SwConnectionState.Failed);
            AddAssistantMessage("system", $"Failed to close SOLIDWORKS cleanly: {ex.Message}");
        }
    }

    /// <summary>
    /// Closes the companion panel.
    /// </summary>
    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Warns the user before closing Nodestar while SOLIDWORKS is still running.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_connector.HasActiveProcess)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "The SOLIDWORKS instance will remain open if you close Nodestar now.\n\nContinue closing Nodestar and leave SOLIDWORKS running?",
            "Close Nodestar",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }

        _connector.DetachAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Handles the primary send action for the preview chat composer.
    /// </summary>
    private void SendButton_OnClick(object sender, RoutedEventArgs e)
    {
        SubmitPrompt();
    }

    /// <summary>
    /// Keeps the send button disabled until the composer contains actual content.
    /// </summary>
    private void PromptTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSendButtonState();
    }

    /// <summary>
    /// Sends prompts with Enter while still allowing multiline input via Shift+Enter.
    /// </summary>
    private void PromptTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            SubmitPrompt();
        }
    }

    /// <summary>
    /// Snaps the window to the right fifth of the current work area.
    /// </summary>
    private void DockToRightSideOfScreen()
    {
        var workArea = SystemParameters.WorkArea;
        var desiredWidth = Math.Round(workArea.Width * DockedWidthRatio);

        Width = desiredWidth;
        Height = workArea.Height;
        Left = workArea.Right - Width;
        Top = workArea.Top;
    }

    /// <summary>
    /// Launches and connects to SOLIDWORKS using the default installation path.
    /// </summary>
    private async Task LaunchSolidWorksAsync()
    {
        AddAssistantMessage("system", "Launching SOLIDWORKS and waiting for the session to become available...");
        UpdateStatusDisplay(SwConnectionState.Launching);

        try
        {
            await _connector.ConnectAsync(DefaultSolidWorksExecutablePath);
            await SyncSolidWorksWindowAsync();

            var revisionSuffix = string.IsNullOrWhiteSpace(_connector.RevisionNumber)
                ? string.Empty
                : $" Revision {_connector.RevisionNumber}.";

            AddAssistantMessage(
                "system",
                $"SOLIDWORKS connected successfully.{revisionSuffix}");
        }
        catch (FileNotFoundException ex)
        {
            UpdateStatusDisplay(SwConnectionState.Failed);
            AddAssistantMessage(
                "system",
                $"SOLIDWORKS could not be launched because the executable path was not found: {ex.FileName}");
        }
        catch (Exception ex)
        {
            UpdateStatusDisplay(SwConnectionState.Failed);
            AddAssistantMessage(
                "system",
                $"Failed to launch or connect to SOLIDWORKS: {ex.Message}");
        }
    }

    /// <summary>
    /// Resizes the SOLIDWORKS window to occupy the space left of the Nodestar panel.
    /// </summary>
    private async Task SyncSolidWorksWindowAsync()
    {
        if (_isSynchronizingSolidWorksWindow ||
            _connector.State != SwConnectionState.Ready ||
            WindowState == WindowState.Maximized)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var solidWorksWidth = (int)Math.Round(Left - workArea.Left);
        if (solidWorksWidth <= 0)
        {
            return;
        }

        try
        {
            _isSynchronizingSolidWorksWindow = true;
            await _connector.ResizeMainWindowAsync(
                (int)Math.Round(workArea.Left),
                (int)Math.Round(workArea.Top),
                solidWorksWidth,
                (int)Math.Round(workArea.Height));
        }
        catch
        {
            // Window sync is a layout convenience rather than a hard requirement.
        }
        finally
        {
            _isSynchronizingSolidWorksWindow = false;
        }
    }

    /// <summary>
    /// Removes the decorative inset when maximized so the shell fills the bounds cleanly.
    /// </summary>
    private void UpdateWindowSurfaceMargin()
    {
        WindowSurface.Margin = WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(10);
    }

    /// <summary>
    /// Keeps the maximize button glyph in sync with the current state.
    /// </summary>
    private void UpdateMaximizeRestoreGlyph()
    {
        MaximizeRestoreIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    /// <summary>
    /// Updates the status pill text, colors, and action availability.
    /// </summary>
    private void UpdateStatusDisplay(SwConnectionState state)
    {
        string label;
        Color indicatorColor;

        switch (state)
        {
            case SwConnectionState.Ready:
                label = "Connected";
                indicatorColor = (Color)ColorConverter.ConvertFromString("#22C55E");
                break;
            case SwConnectionState.Launching:
                label = "Connecting";
                indicatorColor = (Color)ColorConverter.ConvertFromString("#F59E0B");
                break;
            default:
                label = "Disconnected";
                indicatorColor = (Color)ColorConverter.ConvertFromString("#EF4444");
                break;
        }

        StatusIndicator.Fill = new SolidColorBrush(indicatorColor);
        StatusTextBlock.Text = label;
        StatusPillButton.BorderBrush = new SolidColorBrush(
            Color.FromArgb(0x70, indicatorColor.R, indicatorColor.G, indicatorColor.B));
        CloseSolidWorksMenuItem.IsEnabled = _connector.HasActiveProcess;

        if (StatusIndicatorGlow is DropShadowEffect glow)
        {
            glow.Color = indicatorColor;
        }
    }

    /// <summary>
    /// Adds the first assistant messages that explain the current preview state.
    /// </summary>
    private void SeedConversation()
    {
        if (ConversationPanel.Children.Count > 0)
        {
            return;
        }

        AddAssistantMessage(
            "nodestar",
            "The companion panel is live. This is where prompts, tool activity, and SOLIDWORKS feedback will surface.");
        AddAssistantMessage(
            "system",
            "Nodestar will now launch SOLIDWORKS automatically and keep the CAD window arranged beside this panel.");
    }

    /// <summary>
    /// Submits the current prompt into the preview transcript and adds a placeholder assistant reply.
    /// </summary>
    private void SubmitPrompt()
    {
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        AddUserMessage("you", prompt);
        PromptTextBox.Clear();

        AddAssistantMessage(
            "nodestar",
            "Preview mode acknowledged the prompt. The next layer is wiring this surface into the remote LLM session and the SOLIDWORKS bridge.");

        ConversationScrollViewer.ScrollToEnd();
    }

    /// <summary>
    /// Adds an assistant-style message bubble to the transcript.
    /// </summary>
    private void AddAssistantMessage(string author, string message)
    {
        AddMessageBubble(author, message, isUserMessage: false);
    }

    /// <summary>
    /// Adds a user-style message bubble to the transcript.
    /// </summary>
    private void AddUserMessage(string author, string message)
    {
        AddMessageBubble(author, message, isUserMessage: true);
    }

    /// <summary>
    /// Creates a styled transcript bubble and appends it to the conversation panel.
    /// </summary>
    private void AddMessageBubble(string author, string message, bool isUserMessage)
    {
        var bubbleBackground = (Brush)new BrushConverter().ConvertFromString(
            isUserMessage ? "#1F3A5F" : "#131C2E")!;
        var bubbleBorder = (Brush)new BrushConverter().ConvertFromString(
            isUserMessage ? "#31598A" : "#243042")!;
        var bubbleAlignment = isUserMessage ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var authorBlock = new TextBlock
        {
            Text = author.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 14,
            LineHeight = 22
        };

        var contentPanel = new StackPanel();
        contentPanel.Children.Add(authorBlock);
        contentPanel.Children.Add(messageBlock);

        var bubble = new Border
        {
            HorizontalAlignment = bubbleAlignment,
            MaxWidth = 320,
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(18),
            Background = bubbleBackground,
            BorderBrush = bubbleBorder,
            BorderThickness = new Thickness(1),
            Child = contentPanel
        };

        ConversationPanel.Children.Add(bubble);
    }

    /// <summary>
    /// Synchronizes the send button enabled state with the prompt composer contents.
    /// </summary>
    private void UpdateSendButtonState()
    {
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(PromptTextBox.Text);
    }
}
