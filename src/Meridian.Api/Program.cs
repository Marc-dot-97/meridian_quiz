using Meridian.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using MySql.EntityFrameworkCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// MySQL
var connectionString =
    builder.Configuration.GetConnectionString("MeridianDb")
    ?? throw new InvalidOperationException(
        "Connection string 'MeridianDb' was not found.");

builder.Services.AddDbContext<MeridianDbContext>(options =>
{
    options.UseMySQL(connectionString);
});

// CORS for Blazor WASM client
builder.Services.AddCors(options =>
{
    options.AddPolicy("MeridianClient", policy =>
    {
        policy
            .WithOrigins(
                "https://localhost:7274",
                "http://localhost:5274")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Microsoft Entra authentication
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors("MeridianClient");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();