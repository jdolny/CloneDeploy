# Specialize the shared project into independently serviceable MSI products.
function Set-InstallerRole([xml]$Document, [ValidateSet('App', 'SiteService', 'WindowsWorker')][string]$Role) {
    if ($Role -eq 'WindowsWorker') {
        Set-InstallerRole -Document $Document -Role SiteService
        foreach ($row in $Document.SelectNodes('//ROW')) {
            foreach ($attribute in $row.PSBase.Attributes) {
                $attribute.Value = $attribute.Value.Replace('SiteService', 'WindowsWorker').Replace('Site Service', 'Windows Worker')
            }
        }
        $Document.SelectSingleNode('//ROW[@Property="UpgradeCode"]').Value = '{83994981-5CD9-4BDE-8465-0870F8D1D8CD}'
        $Document.SelectSingleNode('//ROW[@Component="InstallationInformation"]').SetAttribute('ComponentId', '{61DC3978-311C-42DD-93D5-D9974E6B31AE}')
        $Document.SelectSingleNode('//ROW[@Component="WindowsWorkerInformation"]').SetAttribute('ComponentId', '{765D0D7E-731C-4638-B5A7-AE1A02340962}')
        return
    }
    $site = $Role -eq 'SiteService'
    $name = if ($site) { 'CloneDeploy-SiteService' } else { 'CloneDeploy-Server' }
    $title = if ($site) { 'CloneDeploy Site Service' } else { 'CloneDeploy Server' }
    $folder = if ($site) { 'SiteService' } else { 'Server' }
    $Document.SelectSingleNode('//ROW[@Property="INSTALL_ROLE"]').Value = $Role
    $props = $Document.SelectSingleNode('//COMPONENT[@cid="caphyon.advinst.msicomp.MsiPropsComponent"]')
    $fixedRole = $Document.CreateElement('ROW')
    $fixedRole.SetAttribute('Property', 'PackagedRole'); $fixedRole.SetAttribute('Value', $Role)
    [void]$props.AppendChild($fixedRole)
    $Document.SelectSingleNode('//ROW[@Property="ProductName"]').Value = $title
    if ($site) { $Document.SelectSingleNode('//ROW[@Property="UpgradeCode"]').Value = '{CA329D40-C256-455C-B82C-54F5C2F3AA67}' }
    $Document.SelectSingleNode('//ROW[@BuildKey="DefaultBuild"]').SetAttribute('PackageFileName', "$name-x64")
    $Document.SelectSingleNode('//ROW[@Action="SET_APPDIR"]').SetAttribute('MultiBuildTarget', "DefaultBuild:[ProgramFiles64Folder]CloneDeploy\$folder")
    $Document.SelectSingleNode('//ROW[@Feature="MainFeature"]').SetAttribute('Title', $title)
    foreach ($row in @($Document.SelectNodes('//ROW[@Argument="InstallRoleDlg"]'))) { $row.SetAttribute('Argument', 'WelcomeDlg') }
    $Document.SelectSingleNode('//ROW[@Dialog_="WelcomeDlg" and @Control_="Next" and @Event="NewDialog"]').SetAttribute('Argument', 'FolderDlg')
    foreach ($row in @($Document.SelectNodes('//ROW[@Dialog_="InstallRoleDlg" or @Dialog="InstallRoleDlg"]'))) { [void]$row.ParentNode.RemoveChild($row) }
    foreach ($row in @($Document.SelectNodes('//COMPONENT[@cid="caphyon.advinst.msicomp.MsiRadioButtonComponent"]/ROW[@Property="INSTALL_ROLE"]'))) { [void]$row.ParentNode.RemoveChild($row) }
    # No component, registry marker or service-control action belongs to both products.
    $other = if ($site) { 'ServerInformation' } else { 'SiteServiceInformation' }
    foreach ($row in @($Document.SelectNodes("//ROW[@Component='$other' or @Component_='$other']"))) { [void]$row.ParentNode.RemoveChild($row) }
    foreach ($row in @($Document.SelectNodes('//ROW[@Registry="InstallationRole" or @Registry="InstallationPath"]'))) {
        $row.SetAttribute('Key', "SOFTWARE\CloneDeploy\Installation\$Role")
    }
    $markerId = if ($site) { '{8B623616-1B03-4C91-900C-F80E6C997DB1}' } else { '{FE8378D8-D949-4F86-B3A5-FBD33C14EFD0}' }
    $Document.SelectSingleNode('//ROW[@Component="InstallationInformation"]').SetAttribute('ComponentId', $markerId)
}
