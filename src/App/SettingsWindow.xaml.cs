using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        if (e.OriginalSource is DependencyObject source &&
            GetAncestor<Button>(source) is not null)
            return;

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private static T? GetAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T t) return t;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
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
