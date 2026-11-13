# Pre-release packages

`Digi21.WinUI.Docking.1.6.1-alpha.0.7.nupkg` is the docking library built from the `development` branch of the fork
(<https://github.com/drolevar/DockingPanels>), commit `5b44d67`, while the changes below are not merged and released:

- the NativeAOT fix (`2e99a5c`, `TemplatePart<T>` and `As<T>`: the library no longer needs the app to keep the classes it casts to);
- `DocumentHost.Arrange`, `DockSite.DockDocument` and `DockSite.AttachDocument`
  (issue <https://github.com/digi21/DockingPanels/issues/1>, pull request <https://github.com/digi21/DockingPanels/pull/2>);
- `DockSite.AutoHideStripPlacement` and the `StripPlacement` attribute of a saved auto-hide group: a collapsed panel keeps its tab
  strip inside the layout, under the area it came out of, instead of along an edge of the whole window (one commit; no
  upstream issue or pull request yet).

The version is MinVer's: seven commits after `v1.6.0`. This build was made on 2026-11-13 with `dotnet pack` (SDK 10) from a clean
checkout of that commit, not by the fork's CI (the earlier `0.6` was the owner's build, handed over as a zip); the commit is in the
package's nuspec and in the DLL's informational version, which is how to check it.

`NuGet.Config` maps the package id `Digi21.WinUI.Docking` to this folder only (a source mapping), and every other package to
nuget.org. **When a release that has those changes is on nuget.org: delete this folder, the `docking-prerelease` source and its
mapping in `NuGet.Config`, and move the version in `Directory.Packages.props` to the release.**
