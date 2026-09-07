using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MAI.BusinessLogic.Dtos;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using LoginDto = MAI.DataAccessLayer.DTOs.LoginDto;

namespace MAI.Api.Controllers
{
    [ApiController]
    [Route("api/Auth")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;

        public AuthController(AppDbContext context, IConfiguration config)
        {
            _context = context;
            _config  = config;
        }

        // POST api/Auth/login
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto request)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var user = await _context.Users.FirstOrDefaultAsync(u =>
                u.Username == request.Username && u.PasswordHash == request.Password);

            // Scriem audit log indiferent de rezultat
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user?.Id,
                Username  = request.Username,
                Action    = AuditAction.Login,
                Details   = user != null
                    ? "SUCCES: Autentificare reușită"
                    : "ESEC: Credențiale incorecte",
                IpAddress = ip,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync();

            if (user == null)
                return BadRequest(new { message = "Nume de utilizator sau parolă incorectă." });

            var token = GenerateJwtToken(user);

            return Ok(new
            {
                id       = user.Id,
                username = user.Username,
                fullName = user.FullName ?? user.Username,
                role     = user.Role,      // număr: 1/2/3
                token,
            });
        }

        // PATCH api/Auth/change-password
        [Authorize]
        [HttpPatch("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
        {
            var userIdStr = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdStr == null || !Guid.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound();

            // Verificăm parola curentă (plain text, demo)
            if (user.PasswordHash != dto.CurrentPassword)
                return BadRequest(new { message = "Parola curentă este incorectă." });

            if (dto.NewPassword.Length < 8)
                return BadRequest(new { message = "Parola nouă trebuie să aibă minim 8 caractere." });

            user.PasswordHash = dto.NewPassword;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = user.Username,
                Action    = AuditAction.UserUpdated,
                Details   = "SUCCES: Parolă schimbată",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync();
            return Ok(new { message = "Parola a fost actualizată cu succes." });
        }

        // ── JWT ──────────────────────────────────────────────────────────────

        private string GenerateJwtToken(MAI.Domain.Entities.User user)
        {
            var jwtKey = _config["Jwt:Key"]
                ?? throw new InvalidOperationException("Jwt:Key lipsește din appsettings!");

            var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name,           user.Username),
                new Claim(ClaimTypes.Role,           user.Role.ToString()),
            };

            var token = new JwtSecurityToken(
                claims:            claims,
                expires:           DateTime.UtcNow.AddHours(8),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}