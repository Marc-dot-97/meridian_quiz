using Meridian.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.System;

[ApiController]
[Route("api/database")]
public class DatabaseController : ControllerBase
{
    private readonly MeridianDbContext _dbContext;

    public DatabaseController(MeridianDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [AllowAnonymous]
    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        try
        {
            bool connected =
                await _dbContext.Database.CanConnectAsync();

            return Ok(new
            {
                connected,
                database = "online_cpd_quiz"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                connected = false,
                error = ex.Message
            });
        }
    }
}