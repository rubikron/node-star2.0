using System.Windows;
using System.Windows.Input;
using LlmOrchestrator;

namespace Nodestar.App;

public partial class SettingsWindow : Window
{
    public LlmSettings Settings { get; }

    public SettingsWindow(LlmSettings current)
    {
        InitializeComponent();
        Settings = current;

        BaseUrlBox.Text = current.BaseUrl;
        ApiKeyBox.Password = current.ApiKey;
        ModelBox.Text = current.Model;
    }

    private void Header_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        Settings.BaseUrl = BaseUrlBox.Text.Trim();
        Settings.ApiKey = ApiKeyBox.Password;
        Settings.Model = ModelBox.Text.Trim();
        Settings.Save();
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
