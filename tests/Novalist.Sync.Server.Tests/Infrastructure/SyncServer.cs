using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// The real server (Program.cs) in memory, pointed at the test database and
// configured to trust TestTokens instead of an external identity provider.
public sealed class SyncServer(DatabaseFixture database) : WebApplicationFactory<Program>
{
    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", database.ConnectionString);

        builder.ConfigureLogging(logging => logging
            .ClearProviders()
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(Logs));

        builder.ConfigureTestServices(services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                options.Configuration.SigningKeys.Add(TestTokens.SigningKey);
                options.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                options.TokenValidationParameters.ValidAudience = TestTokens.Audience;
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
            }));
    }

    public HttpClient CreateClient(string? token)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public HubConnection CreateHubConnection(string? token, Guid? deviceId)
    {
        var query = deviceId is null ? "" : $"?{Protocol.DeviceIdParameter}={deviceId}";

        return new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, Protocol.HubPath + query), options =>
            {
                // The in-memory test server speaks HTTP, so long polling instead of WebSockets.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .AddMessagePackProtocol()
            .Build();
    }
}
