using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MAI.Api.Controllers
{
    [Authorize(Roles = "Administrator,SefDirectie")]
    [ApiController]
    [Route("api/[controller]")]
    public class AuditController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetLogs()
        {
            return Ok(new[]
            {
                new { Timestamp = System.DateTime.UtcNow, Action = "FileUpload", User = "ion.popescu", Details = "A incarcat document.pdf" }
            });
        }
    }
}