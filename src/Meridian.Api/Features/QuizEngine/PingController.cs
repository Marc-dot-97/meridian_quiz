using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Features.QuizEngine;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PingController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new {message = 
    "Meridian API is alive", authenticated = true });
}