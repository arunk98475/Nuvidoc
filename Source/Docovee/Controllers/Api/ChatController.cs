using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IAnthropicChatService _chatService;
    private readonly IPatientDoctorContactService _contactViews;

    public ChatController(IAnthropicChatService chatService, IPatientDoctorContactService contactViews)
    {
        _chatService = chatService;
        _contactViews = contactViews;
    }

    [HttpPost("message")]
    public async Task<ActionResult<ChatMessageResponse>> SendMessage([FromBody] ChatMessageRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) && request.SelectedDoctorId == null && string.IsNullOrWhiteSpace(request.Action))
            return BadRequest(new { message = "Message is required." });

        try
        {
            var result = await _chatService.SendMessageAsync(request, HttpContext, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Unable to process chat message.", detail = ex.Message });
        }
    }

    [HttpPost("record-contact-view")]
    public async Task<IActionResult> RecordContactView(
        [FromBody] ChatRecordContactViewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionKey == Guid.Empty || request.DoctorId <= 0)
            return BadRequest(new { message = "Session key and doctor id are required." });

        await _contactViews.TryRecordContactViewBySessionAsync(request.SessionKey, request.DoctorId, cancellationToken);
        return Ok(new { recorded = true });
    }
}
