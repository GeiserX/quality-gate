# Development

## Building from source

Requires the [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/GeiserX/quality-gate.git
cd quality-gate
dotnet build -c Release
dotnet test
```

The compiled plugin lands in
`Jellyfin.Plugin.QualityGate/bin/Release/net10.0/Jellyfin.Plugin.QualityGate.dll`. Copy it next
to a `meta.json` from a release zip to install a local build.

CI builds with .NET 10.0.x, runs the test suite with coverage, and reports to Codecov. A
contribution needs the build and the tests green, and the patch covered.

## Releases

Pushing a version bump to `main` cuts a release and publishes the plugin manifest to GitHub
Pages, which is what the repository URL in [Getting started](getting-started.md) serves. The same run
builds this documentation site with [Zensical](https://zensical.org/) and deploys it beside the
manifest, so a broken page stops the run before anything is tagged; pull requests run that build as
the `Docs` check. To build it yourself:

```bash
pip install -r docs/requirements-docs.txt
python -m zensical build --clean --strict
python -I .github/scripts/site-check.py site
```

The strict build fails on a broken link or anchor; `site-check.py` fails on what the build lets
through: a page left out of the nav, a nav entry for a missing file, a missing image, a link rooted
at `/`.

The version is declared in three places and they must agree:

- `Jellyfin.Plugin.QualityGate/Jellyfin.Plugin.QualityGate.csproj` (`AssemblyVersion`,
  `FileVersion`, `Version`)
- `Jellyfin.Plugin.QualityGate/build.yaml`
- `Jellyfin.Plugin.QualityGate/meta.json`

The published manifest is **derived from the releases that exist**, not from the copy in the
repo. Every non-draft release tagged `vX.Y.Z.W`, from 2.0.2.0 up, with a plugin zip whose
`build.yaml` states a `targetAbi` is included, its checksum computed from the bytes actually
served and its `targetAbi` read from that `build.yaml`.

That is deliberate. It used to be the committed copy plus one new entry, which made a bookkeeping
commit load-bearing: when that commit did not land, the next release rebuilt from a stale base and
the missing version disappeared from the catalogue. That is how 3.3.6.0 and 3.4.0.0 went, and the
step that was supposed to record it had been failing silently for three releases. Deriving the
manifest makes losing a version impossible, so there is no longer a pull request to remember.

`manifest.json` in the repo now only seeds the editorial header, the name, overview and
description. Its version list is ignored.
