using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IAnthropicChatService _chatService;

    public ChatController(IAnthropicChatService chatService) => _chatService = chatService;

    [HttpPost("message")]
    public async Task<ActionResult<ChatMessageResponse>> SendMessage([FromBody] ChatMessageRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) && request.SelectedDoctorId == null && string.IsNullOrWhiteSpace(request.Action))
            return BadRequest(new { message = "Message is required." });

        try
        {
            var result = await _chatService.SendMessageAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Unable to process chat message.", detail = ex.Message });
        }
    }
}
