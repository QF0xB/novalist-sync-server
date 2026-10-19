{pkgs, ...}: {
  languages.dotnet = {
    enable = true;
    package = pkgs.dotnetCorePackages.sdk_10_0;
  };

  packages = [pkgs.dotnet-ef];

  scripts.contracts-pack.exec = ''
    set -euo pipefail

    if (( $# > 1 )); then
      echo "Usage: contracts-pack [package-version]" >&2
      exit 1
    fi

    version="''${1:-1.0.0-local.$(date -u +%Y%m%d%H%M%S).r$RANDOM$RANDOM}"
    feed="''${NOVALIST_NUGET_FEED:-$DEVENV_ROOT/../.nuget-feed}"
    package="$feed/Novalist.Sync.Contracts.$version.nupkg"

    mkdir -p "$feed"
    if [[ -e "$package" ]]; then
      echo "Package version $version already exists. Choose a new version to avoid NuGet cache reuse." >&2
      exit 1
    fi

    dotnet pack "$DEVENV_ROOT/src/Novalist.Sync.Contracts" \
      --configuration Release \
      --output "$feed" \
      -p:PackageVersion="$version"

    echo "Local Contracts version: $version"
    echo "Local feed: $feed"
  '';

  env = {
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
    ConnectionStrings__Default = "Host=127.0.0.1;Port=5432;Database=novalist_sync;Username=novalist;Password=novalist";
  };

  # Local PostgreSQL for development (`devenv up`).
  services.postgres = {
    enable = true;
    package = pkgs.postgresql_17;
    listen_addresses = "127.0.0.1";
    port = 5432;
    initialDatabases = [
      {
        name = "novalist_sync";
        user = "novalist";
        pass = "novalist";
      }
    ];
    # Integration tests create and drop a fresh database per test run.
    # Runs only when the data directory is first initialized.
    initialScript = "ALTER ROLE novalist CREATEDB;";
  };

  # A stable path for IDEs: Rider needs the real SDK folder (with sdk/ next to
  # dotnet), whose /nix/store path changes on every SDK update.
  enterShell = ''
    ln -sfn "$DOTNET_ROOT" "$DEVENV_STATE/dotnet"
  '';
}
