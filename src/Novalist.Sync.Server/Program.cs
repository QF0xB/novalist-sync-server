using Novalist.Sync.Server.Data;

using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Updates;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("No connection string found in configuration.");

builder.Services.AddDbContext<SyncDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<UpdateLog>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
