using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CloneDeploy_Agent.UI;

/// <summary>Plain text with paired **bold** markers and automatic HTTP(S) links; never parses markup.</summary>
public static class NotificationText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(NotificationText), new PropertyMetadata(null, TextChanged));
    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);
    private static readonly Regex Url = new("(?<![\\p{L}\\p{N}_])https?://[^\\s<>\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static void TextChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBlock block) return;
        block.Inlines.Clear();
        var text = args.NewValue as string ?? "";
        var position = 0;
        while (position < text.Length)
        {
            var opening = text.IndexOf("**", position, StringComparison.Ordinal);
            var closing = opening < 0 ? -1 : text.IndexOf("**", opening + 2, StringComparison.Ordinal);
            if (closing < 0) { AddLinks(block.Inlines, text[position..]); break; }
            AddLinks(block.Inlines, text[position..opening]);
            if (closing == opening + 2) AddLinks(block.Inlines, "****");
            else
            {
                var bold = new Bold(); AddLinks(bold.Inlines, text[(opening + 2)..closing]); block.Inlines.Add(bold);
            }
            position = closing + 2;
        }
    }

    private static void AddLinks(InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match match in Url.Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            // Keep balanced parentheses in URLs, but leave sentence punctuation outside the link.
            foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
                while (candidate.EndsWith(close) && candidate.Count(c => c == close) > candidate.Count(c => c == open)) candidate = candidate[..^1];
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host)) continue;
            if (match.Index > position) inlines.Add(new Run(text[position..match.Index]));
            var link = new Hyperlink(new Run(candidate)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
            link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "BrandAccentText");
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { MessageBox.Show(PortalText.Get("Portal.OpenBrowserManually"), "CloneDeploy"); }
            };
            inlines.Add(link); position = match.Index + candidate.Length;
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }
}
