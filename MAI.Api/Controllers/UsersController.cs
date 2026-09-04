using System;
using System.Threading.Tasks;
using MAI.BusinessLogic.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MAI.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        [HttpPost]
        [Authorize(Roles = "Administrator")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto)
        {
            // Doar Administratorul poate crea utilizatori conform cerintei RBAC
            return Ok(new { message = "Utilizator creat cu succes." });
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            return Ok(new[] 
            {
                new UserDto { Id = Guid.NewGuid(), Username = "ion.popescu", FullName = "Ion Popescu", Department = "Directia Generală" }
            });
        }
    }
}