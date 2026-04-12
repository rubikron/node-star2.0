using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using LlmOrchestrator;
using SwBridge.Connection;
using SwBridge.Tools.Model;
using SwBridge.Tools.Session;

namespace Nodestar.App;

/// <summary>
/// Hosts the Nodestar companion experience and coordinates the paired
/// SOLIDWORKS launch workflow.
/// </summary>
public partial class MainWindow : Window
{
    private const double DockedWidthRatio = 0.20;

    private readonly ISwConnector _connector = new SwConnector();
    private readonly LlmClient _llmClient = new();
    private LlmSettings _settings = LlmSettings.Load();
    private readonly List<ChatMessage> _conversationHistory = [];
    private bool _isStatusMenuOpen;
    private bool _isSynchronizingSolidWorksWindow;

    // Lazy so it picks up the connector after it's fully initialized.
    private GetModelStateTool ModelStateTool => new(_connector);

    // Persistent so _lastSnapshot is preserved across /snapshot patch calls.
    private GetSwStateTool? _swStateTool;
    private GetSwStateTool SwStateTool => _swStateTool ??= new(_connector);

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
    private void HeaderBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<Button>(source) is not null ||
             FindAncestor<ContextMenu>(source) is not null ||
             FindAncestor<TextBox>(source) is not null))
        {
            return;
        }

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
    /// Restores the window to the original docked position.
    /// </summary>
    private void ResetWindowPositionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }

        DockToRightSideOfScreen();
        UpdateWindowSurfaceMargin();
        _ = SyncSolidWorksWindowAsync();
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
        StatusContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
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
    /// Opens the LLM settings dialog.
    /// </summary>
    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.Settings;
        }
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
    /// Snaps the window to the right fifth of the primary work area.
    /// SystemParameters.WorkArea is already in WPF DIPs and is reliable on any DPI setting.
    /// </summary>
    private void DockToRightSideOfScreen()
    {
        var workArea = SystemParameters.WorkArea;
        var desiredWidth = Math.Round(workArea.Width * DockedWidthRatio);

        // Keep MinWidth below the desired docked width so the ratio is always honoured.
        MinWidth = Math.Max(260, Math.Round(desiredWidth * 0.75));

        Width = desiredWidth;
        Height = workArea.Height;
        Left = workArea.Right - desiredWidth;
        Top = workArea.Top;
    }

    /// <summary>
    /// Finds the best available SOLIDWORKS launch target on this machine.
    /// Prefers 3DEXPERIENCE desktop shortcuts (required for SW 2026+) and falls
    /// back to the classic standalone executable for older installations.
    /// </summary>
    private static string? DiscoverSolidWorksLaunchPath()
    {
        // 1. Desktop shortcuts — 3DEXPERIENCE platform creates these and embeds the
        //    required platform context that SW 2026+ enforces at startup.
        var desktopRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };

        foreach (var dir in desktopRoots)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var lnk = Directory.GetFiles(dir, "SOLIDWORKS*.lnk").FirstOrDefault();
            if (lnk is not null)
            {
                return lnk;
            }
        }

        // 2. Start Menu (Programs folder, searched recursively).
        var startMenuRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };

        foreach (var dir in startMenuRoots)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var lnk = Directory
                .GetFiles(dir, "SOLIDWORKS*.lnk", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (lnk is not null)
            {
                return lnk;
            }
        }

        // 3. Classic standalone install (SW 2024 and earlier).
        const string classicExe = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe";
        if (File.Exists(classicExe))
        {
            return classicExe;
        }

        // 4. 3DEXPERIENCE install layout (Dassault Systemes folder, any B-series version).
        const string dsRoot = @"C:\Program Files\Dassault Systemes";
        if (Directory.Exists(dsRoot))
        {
            var exe = Directory
                .GetFiles(dsRoot, "SLDWORKS.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (exe is not null)
            {
                return exe;
            }
        }

        return null;
    }

    /// <summary>
    /// Connects to SOLIDWORKS, launching it if it is not already running.
    /// </summary>
    private async Task LaunchSolidWorksAsync()
    {
        AddAssistantMessage("system", "Connecting to SOLIDWORKS...");
        UpdateStatusDisplay(SwConnectionState.Launching);

        var launchPath = DiscoverSolidWorksLaunchPath();
        if (launchPath is null)
        {
            UpdateStatusDisplay(SwConnectionState.Failed);
            AddAssistantMessage(
                "system",
                "SOLIDWORKS installation not found. If you are using SOLIDWORKS 2026, " +
                "make sure the 3DEXPERIENCE Platform has created a desktop shortcut.");
            return;
        }

        try
        {
            await _connector.ConnectAsync(launchPath);
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
                $"SOLIDWORKS could not be launched because the path was not found: {ex.FileName}");
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
    /// Converts WPF DIPs to physical pixels before calling SetWindowPos, because
    /// SetWindowPos always works in physical pixels regardless of DPI settings.
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
        var swWidthDips = Left - workArea.Left;
        if (swWidthDips <= 0)
        {
            return;
        }

        // SetWindowPos uses physical pixels — multiply DIPs by the DPI scale.
        var (sx, sy) = GetDpiScale();

        try
        {
            _isSynchronizingSolidWorksWindow = true;
            await _connector.ResizeMainWindowAsync(
                (int)Math.Round(workArea.Left   * sx),
                (int)Math.Round(workArea.Top    * sy),
                (int)Math.Round(swWidthDips     * sx),
                (int)Math.Round(workArea.Height * sy));
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
    /// Left margin is 0 so the panel sits flush against the SOLIDWORKS window edge.
    /// </summary>
    private void UpdateWindowSurfaceMargin()
    {
        WindowSurface.Margin = WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(0, 6, 6, 6);
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
    /// Submits the current prompt to the configured LLM and streams the reply into the transcript.
    /// </summary>
    private async void SubmitPrompt()
    {
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        // ── Dev command: /state ───────────────────────────────────────────────
        if (prompt.Equals("/state", StringComparison.OrdinalIgnoreCase))
        {
            PromptTextBox.Clear();
            SendButton.IsEnabled = false;
            AddAssistantMessage("system", "Capturing model state...");
            ConversationScrollViewer.ScrollToEnd();

            try
            {
                var result = await ModelStateTool.ExecuteAsync(
                    new Dictionary<string, string>());
                AddAssistantMessage("nodestar", result);
            }
            catch (Exception ex)
            {
                AddAssistantMessage("system", $"get_model_state failed: {ex.Message}");
            }
            finally
            {
                UpdateSendButtonState();
                ConversationScrollViewer.ScrollToEnd();
            }
            return;
        }
        // ── Dev command: /snapshot full | /snapshot patch ────────────────────
        if (prompt.StartsWith("/snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var parts = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var mode  = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "full";

            if (mode is not ("full" or "patch"))
            {
                AddAssistantMessage("system", "Usage: /snapshot full  or  /snapshot patch");
                return;
            }

            PromptTextBox.Clear();
            SendButton.IsEnabled = false;
            AddAssistantMessage("system", $"Capturing snapshot ({mode})...");
            ConversationScrollViewer.ScrollToEnd();

            try
            {
                var result = await SwStateTool.ExecuteAsync(
                    new Dictionary<string, string> { ["mode"] = mode });

                System.Diagnostics.Debug.WriteLine($"[/snapshot {mode}]\n{result}");
                AddAssistantMessage("system", $"Snapshot ({mode}) written to debug output.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[/snapshot {mode}] ERROR: {ex}");
                AddAssistantMessage("system", $"get_sw_state failed: {ex.Message}");
            }
            finally
            {
                UpdateSendButtonState();
                ConversationScrollViewer.ScrollToEnd();
            }
            return;
        }
        // ─────────────────────────────────────────────────────────────────────

        AddUserMessage("you", prompt);
        _conversationHistory.Add(new ChatMessage("user", prompt));
        PromptTextBox.Clear();
        SendButton.IsEnabled = false;
        ConversationScrollViewer.ScrollToEnd();

        if (string.IsNullOrWhiteSpace(_settings.BaseUrl) || string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            AddAssistantMessage("nodestar", "No LLM configured. Open Settings (gear icon) and enter your API base URL, key, and model.");
            UpdateSendButtonState();
            ConversationScrollViewer.ScrollToEnd();
            return;
        }

        try
        {
            var reply = await _llmClient.SendAsync(_settings, _conversationHistory);
            _conversationHistory.Add(new ChatMessage("assistant", reply));
            AddAssistantMessage("nodestar", reply);
        }
        catch (Exception ex)
        {
            AddAssistantMessage("system", $"LLM request failed: {ex.Message}");
        }
        finally
        {
            UpdateSendButtonState();
            ConversationScrollViewer.ScrollToEnd();
        }
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
    /// System messages render as a centered muted note; user messages as a right-aligned
    /// accent bubble; assistant messages as a left-aligned panel bubble with a label.
    /// </summary>
    private void AddMessageBubble(string author, string message, bool isUserMessage)
    {
        // ── System / status note ──────────────────────────────────────────────
        if (author == "system")
        {
            var noteBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(8, 0, 8, 12)
            };
            ConversationPanel.Children.Add(noteBlock);
            return;
        }

        // ── User message ──────────────────────────────────────────────────────
        if (isUserMessage)
        {
            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontSize = 13,
                LineHeight = 20
            };

            var bubble = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 300,
                Margin = new Thickness(40, 0, 0, 12),
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(16, 16, 4, 16),
                Background = (Brush)new BrushConverter().ConvertFromString("#1A3A6E")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#2D5A9A")!,
                BorderThickness = new Thickness(1),
                Child = messageBlock
            };

            ConversationPanel.Children.Add(bubble);
            return;
        }

        // ── Assistant message ─────────────────────────────────────────────────
        var labelBlock = new TextBlock
        {
            Text = author.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var bodyBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 13,
            LineHeight = 20
        };

        var contentPanel = new StackPanel();
        contentPanel.Children.Add(labelBlock);
        contentPanel.Children.Add(bodyBlock);

        var assistantBubble = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 320,
            Margin = new Thickness(0, 0, 40, 12),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(16, 16, 16, 4),
            Background = (Brush)new BrushConverter().ConvertFromString("#0E1928")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#1C2E48")!,
            BorderThickness = new Thickness(1),
            Child = contentPanel
        };

        ConversationPanel.Children.Add(assistantBubble);
    }

    /// <summary>
    /// Synchronizes the send button enabled state with the prompt composer contents.
    /// </summary>
    private void UpdateSendButtonState()
    {
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(PromptTextBox.Text);
    }

    /// <summary>
    /// Returns the DPI scale for the monitor this window is currently on
    /// (physical pixels per WPF DIP). VisualTreeHelper.GetDpi is reliable
    /// after the window is loaded and works correctly with per-monitor DPI.
    /// </summary>
    private (double X, double Y) GetDpiScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>
    /// Finds an ancestor of the given type in the visual tree.
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed)
            {
                return typed;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
