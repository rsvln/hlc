// Controllers/ProfilesController.cs
namespace HomeLabControlAgent.Controllers;

using Microsoft.AspNetCore.Mvc;
using HomeLabControlAgent.Models;
using HomeLabControlAgent.Services;

[ApiController]
[Route("api/[controller]")]
public class ProfilesController : ControllerBase
{
    private readonly IProfileStorage _profileStorage;
    private readonly ProfileEngine _profileEngine;
    private readonly ILogger<ProfilesController> _logger;

    public ProfilesController(IProfileStorage profileStorage, ProfileEngine profileEngine, ILogger<ProfilesController> logger)
    {
        _profileStorage = profileStorage;
        _profileEngine = profileEngine;
        _logger = logger;
    }

    /// <summary>
    /// Состояние включённых профилей: ok, sensorMissing, failsafe (датчик пропал — вентилятор на 100%), fanMissing.
    /// Выключенных профилей в ответе нет.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<IReadOnlyList<ProfileStatus>> GetStatuses() => Ok(_profileEngine.GetStatuses());

    [HttpGet]
    public async Task<IActionResult> GetProfiles()
    {
        try
        {
            var profiles = await _profileStorage.GetAllAsync();
            return Ok(profiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get profiles");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetProfile(string id)
    {
        try
        {
            var profile = await _profileStorage.GetByIdAsync(id);
            
            if (profile == null)
                return NotFound(new { error = $"Profile {id} not found" });

            return Ok(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get profile {ProfileId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateProfile([FromBody] FanProfile profile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                return BadRequest(new { error = "Profile name is required" });

            if (string.IsNullOrWhiteSpace(profile.SensorId))
                return BadRequest(new { error = "SensorId is required" });

            if (string.IsNullOrWhiteSpace(profile.FanControllerId))
                return BadRequest(new { error = "FanControllerId is required" });

            // Валидация в зависимости от типа кривой
            if (profile.CurveType == CurveType.Custom)
            {
                if (string.IsNullOrWhiteSpace(profile.CustomFormula))
                    return BadRequest(new { error = "Custom formula is required for Custom curve type" });
            }
            else
            {
                if (profile.Points == null || profile.Points.Count < 2)
                    return BadRequest(new { error = "At least 2 curve points are required" });
            }

            // Генерируем новый ID если не указан
            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString();

            await _profileStorage.SaveAsync(profile);

            _logger.LogInformation("Created profile {ProfileId}: {ProfileName}", profile.Id, profile.Name);
            return CreatedAtAction(nameof(GetProfile), new { id = profile.Id }, profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create profile");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProfile(string id, [FromBody] FanProfile profile)
    {
        try
        {
            var existing = await _profileStorage.GetByIdAsync(id);
            if (existing == null)
                return NotFound(new { error = $"Profile {id} not found" });

            // Валидация в зависимости от типа кривой
            if (profile.CurveType == CurveType.Custom)
            {
                if (string.IsNullOrWhiteSpace(profile.CustomFormula))
                    return BadRequest(new { error = "Custom formula is required for Custom curve type" });
            }
            else
            {
                if (profile.Points == null || profile.Points.Count < 2)
                    return BadRequest(new { error = "At least 2 curve points are required" });
            }

            profile.Id = id; // Убеждаемся что ID не меняется
            await _profileStorage.SaveAsync(profile);

            _logger.LogInformation("Updated profile {ProfileId}: {ProfileName}", profile.Id, profile.Name);
            return Ok(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update profile {ProfileId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProfile(string id)
    {
        try
        {
            var existing = await _profileStorage.GetByIdAsync(id);
            if (existing == null)
                return NotFound(new { error = $"Profile {id} not found" });

            await _profileStorage.DeleteAsync(id);
            
            _logger.LogInformation("Deleted profile {ProfileId}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete profile {ProfileId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPut("{id}/enable")]
    public async Task<IActionResult> EnableProfile(string id)
    {
        try
        {
            var profile = await _profileStorage.GetByIdAsync(id);
            if (profile == null)
                return NotFound(new { error = $"Profile {id} not found" });

            profile.Enabled = true;
            await _profileStorage.SaveAsync(profile);
            
            _logger.LogInformation("Enabled profile {ProfileId}: {ProfileName}", profile.Id, profile.Name);
            return Ok(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable profile {ProfileId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPut("{id}/disable")]
    public async Task<IActionResult> DisableProfile(string id)
    {
        try
        {
            var profile = await _profileStorage.GetByIdAsync(id);
            if (profile == null)
                return NotFound(new { error = $"Profile {id} not found" });

            profile.Enabled = false;
            await _profileStorage.SaveAsync(profile);
            
            _logger.LogInformation("Disabled profile {ProfileId}: {ProfileName}", profile.Id, profile.Name);
            return Ok(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable profile {ProfileId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}