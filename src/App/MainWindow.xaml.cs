using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nodestar.App;

/// <summary>
/// Hosts the initial Nodestar companion-panel experience.
/// </summary>
public partial class MainWindow : Window
{
    private const double DockedWidthRatio = 0.20;

    /// <summary>
    /// Initializes the window and seeds the preview conversation.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        SeedConversation();
        UpdateSendButtonState();
    }

    /// <summary>
    /// Positions the window as a docked panel on the right side of the primary work area.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DockToRightSideOfScreen();
        PromptTextBox.Focus();
    }

    /// <summary>
    /// Allows the custom header surface to drag the borderless window.
    /// </summary>
    private void HeaderBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
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
    /// Closes the companion panel.
    /// </summary>
    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
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
            "Connection and model actions are intentionally disabled in this preview. The goal here is to lock in layout and interaction feel first.");
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
