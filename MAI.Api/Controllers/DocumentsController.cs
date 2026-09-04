using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MAI.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetDocuments()
        {
            return Ok(new[]
            {
                new { Id = 1, Title = "Regulament Intern", DocumentNumber = "ORD-2026-01", CurrentVersion = 2 }
            });
        }

        [HttpPost("upload-version")]
        public async Task<IActionResult> AddVersion()
        {
            return Ok(new { message = "Versiune nouă adăugată." });
        }
    }
}