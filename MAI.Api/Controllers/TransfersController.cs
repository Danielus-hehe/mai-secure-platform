using System;
using System.Security.Claims;
using System.Threading.Tasks;
using MAI.BusinessLogic.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MAI.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class TransfersController : ControllerBase
    {
        private readonly TransferService _transferService;

        public TransfersController(TransferService transferService)
        {
            _transferService = transferService;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile file, [FromForm] Guid recipientId)
        {
            if (file == null || file.Length == 0) return BadRequest("Fișier invalid.");

            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

            using var stream = file.OpenReadStream();
            var result = await _transferService.SendFileAsync(userId, recipientId, stream, file.FileName, ip);

            return Ok(result);
        }

        [HttpGet("download/{id}")]
        public async Task<IActionResult> Download(Guid id)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

            var (fileBytes, fileName) = await _transferService.DownloadFileAsync(id, userId, ip);
            return File(fileBytes, "application/octet-stream", fileName);
        }
    }
}