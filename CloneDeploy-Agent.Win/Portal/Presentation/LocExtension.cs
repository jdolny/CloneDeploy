using System.Windows.Markup;

namespace CloneDeploy_Agent.UI;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => PortalText.Get(key);
}
