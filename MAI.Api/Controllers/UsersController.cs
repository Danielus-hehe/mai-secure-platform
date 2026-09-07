using MAI.BusinessLogic.Dtos;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{
    [Authorize]          // Orice utilizator autentificat cu JWT valid
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _context;

        public UsersController(AppDbContext context)
        {
            _context = context;
        }

        // POST api/Users — Creare utilizator nou (fără criptare parolă, demo)
        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto)
        {
            // Validare câmpuri obligatorii
            if (string.IsNullOrWhiteSpace(dto.Username) ||
                string.IsNullOrWhiteSpace(dto.Password))
            {
                return BadRequest(new { message = "Username și parola sunt obligatorii." });
            }

            // Verificăm dacă username-ul există deja
            var exists = await _context.Users
                .AnyAsync(u => u.Username == dto.Username);

            if (exists)
            {
                return Conflict(new { message = $"Username-ul '{dto.Username}' există deja." });
            }

            // Creăm entitatea — parola stocată simplu (fără hash, conform cererii)
            var user = new User
            {
                Id           = Guid.NewGuid(),
                Username     = dto.Username.Trim(),
                Email        = dto.Email?.Trim() ?? string.Empty,
                PasswordHash = dto.Password,           // plain text pentru demo
                FullName     = dto.FullName?.Trim() ?? string.Empty,
                Department   = dto.Department?.Trim() ?? string.Empty,
                Role         = dto.Role,
                IsActive     = true,
                CreatedAt    = DateTime.UtcNow,
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = $"Contul @{user.Username} a fost creat cu succes.",
                id      = user.Id,
            });
        }

        // GET api/Users — Lista tuturor utilizatorilor
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
                    FullName   = u.FullName ?? string.Empty,
                    Department = u.Department ?? string.Empty,
                    Role       = u.Role,
                    IsActive   = u.IsActive,
                    CreatedAt  = u.CreatedAt,
                })
                .ToListAsync();

            return Ok(users);
        }

        // PATCH api/Users/{id}/deactivate — Dezactivare cont
        [HttpPatch("{id:guid}/deactivate")]
        public async Task<IActionResult> Deactivate(Guid id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            user.IsActive = false;
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Contul @{user.Username} a fost dezactivat." });
        }
    }
}