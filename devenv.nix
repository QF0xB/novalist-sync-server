{pkgs, ...}: {
  languages.dotnet = {
    enable = true;
    package = pkgs.dotnetCorePackages.sdk_10_0;
  };

  packages = [pkgs.dotnet-ef];

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
  };

  # A stable path for IDEs: Rider needs the real SDK folder (with sdk/ next to
  # dotnet), whose /nix/store path changes on every SDK update.
  enterShell = ''
    ln -sfn "$DOTNET_ROOT" "$DEVENV_STATE/dotnet"
  '';
}
