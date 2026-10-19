using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Updates;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("No connection string found in configuration.");

builder.Services.AddDbContext<SyncDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<UpdateLog>();

builder.Services.AddScoped<UserDirectory>();
builder.Services.AddScoped<DeviceRegistry>();
builder.Services.AddScoped<ProjectAccess>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);

builder.Services
    .AddSignalR(options => options.AddFilter<ClientErrorFilter>())
    .AddMessagePackProtocol();
builder.Services.AddAuthorization();

builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddScoped<AccessRevocation>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<SyncHub>(Protocol.HubPath);
app.MapDeviceEndpoints();

app.Run();
