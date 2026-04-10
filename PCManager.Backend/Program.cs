using PCManager.Core.Services;
using PCManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Add cross-platform services
builder.Services.AddSingleton<IOSService, OSService>();
builder.Services.AddSingleton<INetworkService, NetworkService>();
builder.Services.AddHostedService<PCManager.Backend.Workers.TelemetryWorker>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer("Server=localhost,1433;Database=PCManagerDb;User Id=sa;Password=Your_password123;TrustServerCertificate=True;"));

var app = builder.Build();

app.UseAuthorization();
app.MapControllers();

app.Run();
