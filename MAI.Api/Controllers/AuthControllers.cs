using System.Threading.Tasks;
using MAI.BusinessLogic.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace MAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            // Aici se verifica credentialele din DB si se genereaza Token JWT.
            // Exemplu de raspuns mock/temporar pentru testare:
            if (dto.Username == "admin" && dto.Password == "admin123")
            {
                return Ok(new AuthResponseDto
                {
                    Token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.dummy_token",
                    User = new UserDto
                    {
                        Username = "admin",
                        FullName = "Administrator Sistem",
                        Role = Domain.Enums.UserRole.Administrator,
                        Department = "IT"
                    }
                });
            }

            return Unauthorized(new { message = "Nume de utilizator sau parolă incorectă." });
        }
    }
}