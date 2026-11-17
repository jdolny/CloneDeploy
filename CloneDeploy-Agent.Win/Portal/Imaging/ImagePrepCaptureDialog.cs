using System.Windows;
using System.Windows.Controls;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

internal sealed class ImagePrepCaptureDialog : Window
{
    public int? ImageId { get; private set; }
    public string? NewImageName { get; private set; }
    public string? Format { get; private set; }
    public bool CanCreateImage { get; init; }

    public ImagePrepCaptureDialog(Window owner, AgentCaptureImage[] images)
    {
        Owner = owner;
        Title = PortalText.Get("Imaging.CaptureDestination");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Imaging.CaptureDestinationDescription"), TextWrapping = TextWrapping.Wrap });
        var existing = new RadioButton { Content = PortalText.Get("Imaging.UseExistingImage"), IsChecked = true, Margin = new Thickness(0, 16, 0, 8), GroupName = "destination" };
        panel.Children.Add(existing);
        var list = new ComboBox { ItemsSource = images, DisplayMemberPath = "DisplayName", IsEditable = true, IsReadOnly = true, IsTextSearchEnabled = true };
        panel.Children.Add(list);
        var create = new RadioButton { Content = PortalText.Get("Imaging.CreateNewImage"), Margin = new Thickness(0, 16, 0, 8), GroupName = "destination" };
        panel.Children.Add(create);
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Imaging.ImageName") });
        var name = new TextBox { MaxLength = 200, IsEnabled = false, Margin = new Thickness(0, 5, 0, 10) };
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Imaging.ImageType") });
        var format = new ComboBox { ItemsSource = new[] { PortalText.Get("Imaging.WIMWindowsPartition"), PortalText.Get("Imaging.FFUWholeGPTDisk") }, SelectedIndex = 0, IsEnabled = false, Margin = new Thickness(0, 5, 0, 10) };
        panel.Children.Add(format);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = PortalText.Get("Common.Cancel"), IsCancel = true, Padding = new Thickness(14, 8, 14, 8) };
        var select = new Button { Content = PortalText.Get("Imaging.Continue"), IsDefault = true, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(select); panel.Children.Add(buttons);
        existing.Checked += (_, _) => { list.IsEnabled = true; name.IsEnabled = format.IsEnabled = false; };
        create.Checked += (_, _) => { list.IsEnabled = false; name.IsEnabled = format.IsEnabled = true; name.Focus(); };
        Loaded += (_, _) =>
        {
            create.IsEnabled = CanCreateImage;
            existing.IsEnabled = images.Length > 0;
            if (images.Length == 0 && CanCreateImage) create.IsChecked = true;
            if (images.Length == 0 && !CanCreateImage) error.Text = PortalText.Get("Imaging.CaptureDestinationPermissionRequired");
        };
        select.Click += (_, _) =>
        {
            if (create.IsChecked == true)
            {
                var value = name.Text.Trim();
                if (value.Length == 0 || value.Any(char.IsControl)) { error.Text = PortalText.Get("Imaging.InvalidImageName"); return; }
                NewImageName = value;
                Format = format.SelectedIndex == 0 ? "Wim" : "Ffu";
            }
            else
            {
                if (list.SelectedItem is not AgentCaptureImage image) { error.Text = PortalText.Get("Imaging.SelectAnImage"); return; }
                ImageId = image.Id;
                Format = image.Format;
            }
            DialogResult = true;
        };
    }
}
