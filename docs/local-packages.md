# Local Contracts packages

Before publishing Contracts, use a folder-based NuGet feed to develop the server
and the Novalist client together. Both still consume normal NuGet packages;
there is no cross-repository project reference.

## Layout

```text
novalist/
  .nuget-feed/
  app/
  server/
```

## Pack Contracts

From the server repository:

```bash
devenv shell -- contracts-pack
```

This builds Contracts in Release mode and writes the package to
`../.nuget-feed`. The command prints a fresh version such as
`1.0.0-local.20261019160000.r123456789`. Use that exact version in the client.

An explicit version is also supported:

```bash
devenv shell -- contracts-pack 1.0.0-local.1
```

Existing package files are not overwritten. Every package containing changed
code needs a new version: NuGet can otherwise reuse its cached copy. To choose
a different output folder, set `NOVALIST_NUGET_FEED` inside the shell before
running `contracts-pack`.

## Configure the app checkout

Create `NuGet.Config` at the app repository root:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="NovalistLocal" value="../.nuget-feed" />
  </packageSources>
</configuration>
```

The path is relative to this configuration file. Existing user-level sources,
including nuget.org, remain available because the configuration does not clear
them.

Keep this checkout-specific configuration out of commits by adding the following
line to the app checkout's `.git/info/exclude`:

```gitignore
/NuGet.Config
```

Verify the source from the app repository:

```bash
devenv shell -- dotnet nuget list source
```

Once `Novalist.Sync.Client` exists, add the printed package version to its project:

```xml
<PackageReference Include="Novalist.Sync.Contracts"
                  Version="1.0.0-local.20261019160000.r123456789" />
```

Run `dotnet restore` inside the app's devenv shell. When the contract changes,
pack again and update the reference to the new version.

## CI and publishing

These local packages are for sibling-checkout development. Before a client PR
needs to build in shared CI, publish a prerelease to the shared package feed and
reference that published version instead.
