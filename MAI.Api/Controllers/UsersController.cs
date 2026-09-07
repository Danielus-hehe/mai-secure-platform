using MAI.BusinessLogic.Dtos;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _context;
        public UsersController(AppDbContext context) => _context = context;

        private string CallerUsername =>
            HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // GET api/Users
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var users = await _context.Users
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new UserDto
                {
                    Id         = u.Id,
                    Username   = u.Username,
                    Email      = u.Email,
                    FullName   = u.FullName   ?? string.Empty,
                    Department = u.Department ?? string.Empty,
                    Role       = u.Role,
                    IsActive   = u.IsActive,
                    CreatedAt  = u.CreatedAt,
                })
                .ToListAsync();
            return Ok(users);
        }

        // POST api/Users
        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(dto.Password))
                return BadRequest(new { message = "Username si parola sunt obligatorii." });

            if (await _context.Users.AnyAsync(u => u.Username == dto.Username))
                return Conflict(new { message = $"Username-ul '{dto.Username}' exista deja." });

            var user = new User
            {
                Id           = Guid.NewGuid(),
                Username     = dto.Username.Trim(),
                Email        = dto.Email?.Trim()      ?? string.Empty,
                PasswordHash = dto.Password,
                FullName     = dto.FullName?.Trim()   ?? string.Empty,
                Department   = dto.Department?.Trim() ?? string.Empty,
                Role         = dto.Role,
                IsActive     = true,
                CreatedAt    = DateTime.UtcNow,
            };
            _context.Users.Add(user);
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserCreated,
                Details   = $"SUCCES: Cont creat @{user.Username} ({user.Role})",
                IpAddress = CallerIp,
            });
            await _context.SaveChangesAsync();
            return Ok(new { message = $"Contul @{user.Username} a fost creat cu succes.", id = user.Id });
        }

        // PATCH api/Users/{id}/role
        [HttpPatch("{id:guid}/role")]
        public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeRoleDto dto)
        {
            var user = await _context.Users.FindAsync(id);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });
            var old = user.Role;
            user.Role = dto.Role;
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Rol schimbat @{user.Username}: {old} -> {dto.Role}",
                IpAddress = CallerIp,
            });
            await _context.SaveChangesAsync();
            return Ok(new { message = $"Rolul @{user.Username} a fost actualizat." });
        }

        // PATCH api/Users/{id}/deactivate
        [HttpPatch("{id:guid}/deactivate")]
        public async Task<IActionResult> Deactivate(Guid id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });
            user.IsActive = false;
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Cont dezactivat @{user.Username}",
                IpAddress = CallerIp,
            });
            await _context.SaveChangesAsync();
            return Ok(new { message = $"Contul @{user.Username} a fost dezactivat." });
        }

        // PATCH api/Users/{id}/activate
        [HttpPatch("{id:guid}/activate")]
        public async Task<IActionResult> Activate(Guid id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost gasit." });
            user.IsActive = true;
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"SUCCES: Cont activat @{user.Username}",
                IpAddress = CallerIp,
            });
            await _context.SaveChangesAsync();
            return Ok(new { message = $"Contul @{user.Username} a fost activat." });
        }
    }
}