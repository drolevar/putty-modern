# Pre-release packages

`Digi21.WinUI.Docking.1.6.1-alpha.0.6.nupkg` is the docking library built from the `development` branch of the fork
(<https://github.com/drolevar/DockingPanels>), commit `e771555`, while the pull requests below are not merged and released:

- the NativeAOT fix (`2e99a5c`, `TemplatePart<T>` and `As<T>`: the library no longer needs the app to keep the classes it casts to);
- `DocumentHost.Arrange`, `DockSite.DockDocument` and `DockSite.AttachDocument`
  (issue <https://github.com/digi21/DockingPanels/issues/1>, pull request <https://github.com/digi21/DockingPanels/pull/2>).

The version is MinVer's: six commits after `v1.6.0`. It is built by the owner (`dotnet pack` of that commit) and handed over
as a zip; the commit is in the package's nuspec and in the DLL's informational version, which is how to check it.

`NuGet.Config` maps the package id `Digi21.WinUI.Docking` to this folder only (a source mapping), and every other package to
nuget.org. **When a release that has those changes is on nuget.org: delete this folder, the `docking-prerelease` source and its
mapping in `NuGet.Config`, and move the version in `Directory.Packages.props` to the release.**
