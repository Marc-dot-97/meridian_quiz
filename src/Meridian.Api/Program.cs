using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

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

builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalDev", policy =>
        policy.WithOrigins("http://127.0.0.1:5500", "http://localhost:5500")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors("LocalDev");

app.UseCors("MeridianClient");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();