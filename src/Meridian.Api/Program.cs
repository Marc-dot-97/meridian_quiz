using Meridian.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using MySql.EntityFrameworkCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------
// Controllers
// --------------------------------------------------

builder.Services.AddControllers();


// --------------------------------------------------
// MySQL database
// --------------------------------------------------

var connectionString =
    builder.Configuration.GetConnectionString("MeridianDb")
    ?? throw new InvalidOperationException(
        "Connection string 'MeridianDb' was not found.");

builder.Services.AddDbContext<MeridianDbContext>(options =>
{
    options.UseMySQL(connectionString);
});


// --------------------------------------------------
// Microsoft Entra authentication
// --------------------------------------------------

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();


// --------------------------------------------------
// OpenAPI
// --------------------------------------------------

builder.Services.AddOpenApi();


var app = builder.Build();


// --------------------------------------------------
// Development
// --------------------------------------------------

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}


// --------------------------------------------------
// HTTP pipeline
// --------------------------------------------------

app.UseHttpsRedirection();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.Run();