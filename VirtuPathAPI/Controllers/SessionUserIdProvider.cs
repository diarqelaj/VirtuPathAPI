using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http;

namespace VirtuPathAPI.Hubs
{
  public class SessionUserIdProvider : IUserIdProvider
  {
    private readonly IHttpContextAccessor _http;
    public SessionUserIdProvider(IHttpContextAccessor http) => _http = http;

    public string GetUserId(HubConnectionContext ctx)
    {
      var uid = _http.HttpContext?.Session.GetInt32("UserID");
      return uid?.ToString() ?? "";
    }
  }
}
