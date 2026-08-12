using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.Users.Dtos;
using Meridian.Api.Features.Users.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Users;

[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly MeridianDbContext _dbContext;

    public UsersController(MeridianDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ---------------------------------------------------------
    // GET: api/users
    // ---------------------------------------------------------

    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers()
    {
        var users = await _dbContext.Users
            .AsNoTracking()
            .OrderBy(user => user.DisplayName)
            .Select(user => new UserDto
            {
                Id = user.Id,
                UserName = user.UserName,
                Email = user.Email,
                DisplayName = user.DisplayName,
                UserRole = user.UserRole,
                IsActive = user.IsActive ?? true,
                Department = user.Department,
                FirstName = user.FirstName,
                LastName = user.LastName,
                CreatedAt = user.CreatedAt
            })
            .ToListAsync();

        return Ok(users);
    }

    // ---------------------------------------------------------
    // GET: api/users/5
    // ---------------------------------------------------------

    [AllowAnonymous]
    [HttpGet("{id:long}")]
    public async Task<ActionResult<UserDto>> GetUser(ulong id)
    {
        var user = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == id)
            .Select(user => new UserDto
            {
                Id = user.Id,
                UserName = user.UserName,
                Email = user.Email,
                DisplayName = user.DisplayName,
                UserRole = user.UserRole,
                IsActive = user.IsActive ?? true,
                Department = user.Department,
                FirstName = user.FirstName,
                LastName = user.LastName,
                CreatedAt = user.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (user is null)
        {
            return NotFound(new
            {
                message = $"User with ID {id} was not found."
            });
        }

        return Ok(user);
    }

    // ---------------------------------------------------------
    // POST: api/users
    // ---------------------------------------------------------

    [AllowAnonymous]
    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser(
        CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            return BadRequest(new
            {
                message = "Username is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new
            {
                message = "Email is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.FirstName))
        {
            return BadRequest(new
            {
                message = "First name is required."
            });
        }

        var emailExists = await _dbContext.Users
            .AnyAsync(user => user.Email == request.Email);

        if (emailExists)
        {
            return Conflict(new
            {
                message = "A user with this email already exists."
            });
        }

        var now = DateTime.UtcNow;

        var displayName =
            $"{request.FirstName} {request.LastName}".Trim();

        var user = new User
        {
            UserName = request.UserName.Trim(),

            // This database field is currently required.
            // We can change this once Marc confirms its intended meaning.
            Administrators = string.Empty,

            Email = request.Email.Trim(),

            DisplayName = displayName,

            UserRole = string.IsNullOrWhiteSpace(request.UserRole)
                ? "Advisor"
                : request.UserRole.Trim(),

            IsActive = true,

            CreatedAt = now,

            UpdatedAt = now,

            Department = request.Department.Trim(),

            FirstName = request.FirstName.Trim(),

            LastName = string.IsNullOrWhiteSpace(request.LastName)
                ? null
                : request.LastName.Trim()
        };

        _dbContext.Users.Add(user);

        await _dbContext.SaveChangesAsync();

        var response = MapToDto(user);

        return CreatedAtAction(
            nameof(GetUser),
            new { id = user.Id },
            response);
    }

    // ---------------------------------------------------------
    // PUT: api/users/5
    // ---------------------------------------------------------

    [AllowAnonymous]
    [HttpPut("{id:long}")]
    public async Task<ActionResult<UserDto>> UpdateUser(
        ulong id,
        UpdateUserRequest request)
    {
        var user = await _dbContext.Users
            .FirstOrDefaultAsync(user => user.Id == id);

        if (user is null)
        {
            return NotFound(new
            {
                message = $"User with ID {id} was not found."
            });
        }

        var duplicateEmail = await _dbContext.Users
            .AnyAsync(existingUser =>
                existingUser.Email == request.Email &&
                existingUser.Id != id);

        if (duplicateEmail)
        {
            return Conflict(new
            {
                message = "Another user already uses this email."
            });
        }

        user.UserName = request.UserName.Trim();

        user.Email = request.Email.Trim();

        user.FirstName = request.FirstName.Trim();

        user.LastName =
            string.IsNullOrWhiteSpace(request.LastName)
                ? null
                : request.LastName.Trim();

        user.Department = request.Department.Trim();

        user.DisplayName =
            $"{user.FirstName} {user.LastName}".Trim();

        user.UserRole =
            string.IsNullOrWhiteSpace(request.UserRole)
                ? "Advisor"
                : request.UserRole.Trim();

        user.IsActive = request.IsActive;

        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return Ok(MapToDto(user));
    }

    // ---------------------------------------------------------
    // DTO mapping
    // ---------------------------------------------------------

    private static UserDto MapToDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            DisplayName = user.DisplayName,
            UserRole = user.UserRole,
            IsActive = user.IsActive ?? true,
            Department = user.Department,
            FirstName = user.FirstName,
            LastName = user.LastName,
            CreatedAt = user.CreatedAt
        };
    }
}