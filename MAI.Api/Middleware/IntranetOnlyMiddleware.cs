using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MAI.Api.Middleware
{
    public class IntranetOnlyMiddleware
    {
        private readonly RequestDelegate _next;

        public IntranetOnlyMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var remoteIp = context.Connection.RemoteIpAddress;

            // Daca aveti restrictii severe de IP de intranet, le puteti filtra aici:
            // Ex: IP-uri din plaja 10.x.x.x sau 192.168.x.x sau localhost (::1 / 127.0.0.1)
            
            await _next(context);
        }
    }
}