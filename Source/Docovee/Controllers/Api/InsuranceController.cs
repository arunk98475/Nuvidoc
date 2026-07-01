using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class InsuranceController : ControllerBase
{
    private readonly IInsuranceService _insuranceService;

    public InsuranceController(IInsuranceService insuranceService) => _insuranceService = insuranceService;

    [HttpGet("carriers")]
    public async Task<ActionResult<IReadOnlyList<InsuranceCarrierDto>>> GetCarriers(CancellationToken cancellationToken)
    {
        var carriers = await _insuranceService.GetCarriersAsync(cancellationToken);
        return Ok(carriers);
    }
}
