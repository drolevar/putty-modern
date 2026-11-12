# Pre-release packages

`Digi21.WinUI.Docking.1.7.0-putty.1.nupkg` is the docking library built from the fork
(<https://github.com/drolevar/DockingPanels>, branch `feature/document-arrangement`) with
`DocumentHost.Arrange`, `DockSite.DockDocument` and `DockSite.AttachDocument`, packed with

    dotnet pack src/Digi21.WinUI.Docking -c Release -p:MinVerVersionOverride=1.7.0-putty.1

`NuGet.Config` maps the package id `Digi21.WinUI.Docking` to this folder only (a source mapping), and every
other package to nuget.org. **Delete this folder, the source and its mapping in `NuGet.Config`, and move the
version in `Directory.Packages.props` to the release, when a version that has those calls is on nuget.org.**
