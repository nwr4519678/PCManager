using Microsoft.AspNetCore.Mvc;
using PCManager.Core.Services;

namespace PCManager.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProcessController : ControllerBase
{
    private readonly IOSService _osService;

    public ProcessController(IOSService osService)
    {
        _osService = osService;
    }

    [HttpGet]
    public async Task<IActionResult> GetProcesses()
    {
        return Ok(await _osService.GetActiveProcessesAsync());
    }

    [HttpPost("{id}/kill")]
    public async Task<IActionResult> KillProcess(int id)
    {
        var success = await _osService.KillProcessAsync(id);
        return success ? Ok() : BadRequest("Failed to kill process.");
    }
}
