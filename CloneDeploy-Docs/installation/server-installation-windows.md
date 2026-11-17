# Windows Installation



## 1. Prepare the server

Assign a stable IP address and DNS hostname. Review Requirements & Planning for more info.

Install these machine-wide prerequisites before running the MSI:

- [Latest .NET 10 Hosting Bundle](https://aka.ms/dotnetcore-10-0-windowshosting). This installs the .NET and ASP.NET Core runtimes together, including the required x64 runtimes. CloneDeploy requires **10.0.13 or a newer .NET 10 version, .NET 11 is not supported**.
- [Latest Microsoft Visual C++ x64 Redistributable](https://aka.ms/vc14/vc_redist.x64.exe). CloneDeploy requires **14.44.35211.0 or later**.


## 2. Run the server installer
- Download **CloneDeploy-Server-x64.msi** from the assets of the [latest CloneDeploy release on GitHub](https://github.com/jdolny/CloneDeploy/releases/latest), then run it.
- It is recommended to use all default options during installation.
- Open the server's initial HTTP address, for example `http://clonedeploy.example.org/`, Continue with Initial Configuration in the navigation.
