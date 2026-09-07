using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MAI.DataAccessLayer;
using MAI.DataAccessLayer.DTOs;

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
            _config = config;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto request)
        {
            // 1. Căutăm utilizatorul în baza de date după username și parolă simplă
            var user = await _context.Users.FirstOrDefaultAsync(u => 
                u.Username == request.Username && u.PasswordHash == request.Password);

            if (user == null)
            {
                return BadRequest("Nume de utilizator sau parolă incorectă.");
            }

            // 2. Generăm token-ul JWT folosind metoda deja existentă mai jos
            var token = GenerateJwtToken(user);

            // 3. Returnăm răspunsul complet către frontend
            var response = new 
            {
                id = user.Id,
                username = user.Username,
                role = user.Role, // Preluat automat din baza de date
                token = token
            };

            return Ok(response);
        }

        private string GenerateJwtToken(dynamic user)
        {
            var jwtKey = _config["Jwt:Key"] 
                         ?? throw new InvalidOperationException("Cheia Jwt:Key nu este configurată în appsettings!");

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString())
            };

            var token = new JwtSecurityToken(
                claims: claims,
                expires: DateTime.UtcNow.AddHours(8),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}