using Microsoft.AspNetCore.Mvc;
using HomeLabControl.Auth;
using HomeLabControl.Services;
using Microsoft.AspNetCore.Authorization;
using HomeLabControl.Models;

namespace HomeLabControl.Controllers
{
    [ApiController]
    [Authorize(Policy = Policies.BackupView)]
    [Route("api/[controller]")]
    public class BackupManagerController : ControllerBase
    {
        private readonly BackupManagerService _backupService;
        private readonly ILogger<BackupManagerController> _logger;
        private readonly UserService _users;
        private readonly AuditService _audit;

        public BackupManagerController(BackupManagerService backupService, ILogger<BackupManagerController> logger, UserService users, AuditService audit)
        {
            _backupService = backupService;
            _logger = logger;
            _users = users;
            _audit = audit;
        }

        private bool HostAllowed(string hostName) => HlcAuth.HostAllowed(_users.Find(User.Identity?.Name), hostName);

        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            return Ok(_backupService.GetConfig());
        }

        [HttpGet("hosts")]
        public IActionResult GetHosts()
        {
            return Ok(_backupService.GetHosts().Where(h => HostAllowed(h.Name)));
        }

        [HttpGet("templates")]
        public IActionResult GetTemplates()
        {
            return Ok(_backupService.GetTemplates());
        }

        [HttpGet("storages")]
        public IActionResult GetStorages()
        {
            return Ok(_backupService.GetStorages());
        }

        [HttpGet("ssh-keys")]
        public IActionResult GetSshKeys()
        {
            return Ok(_backupService.GetSshKeys());
        }

        [HttpPost("ssh-keys/generate")]

        [Authorize(Policy = Policies.BackupControl)]
        public async Task<IActionResult> GenerateSshKey([FromBody] GenerateKeyRequest request)
        {
            try
            {
                var keyPath = await _backupService.GenerateSshKeyAsync(request.KeyName);
                _audit.Log(User.Identity?.Name, "backup.key.generate", request.KeyName, "api");
                return Ok(new { message = "SSH key generated successfully", keyPath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to generate SSH key: {request.KeyName}");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpDelete("ssh-keys")]

        [Authorize(Policy = Policies.BackupControl)]
        public IActionResult DeleteSshKey([FromQuery] string keyPath)
        {
            try
            {
                _backupService.DeleteSshKey(keyPath);
                _audit.Log(User.Identity?.Name, "backup.key.delete", keyPath, "api");
                return Ok(new { message = "SSH key deleted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete SSH key: {keyPath}");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("backup/{hostName}")]

        [Authorize(Policy = Policies.BackupControl)]
        public async Task<IActionResult> RunBackup(string hostName)
        {
            try
            {
                if (!HostAllowed(hostName))
                    return Forbid();

                var job = await _backupService.RunBackupAsync(hostName);
                _audit.Log(User.Identity?.Name, "backup.run", hostName, $"api: {job.Status}", job.Status is "Success" or "Skipped");
                return Ok(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to run backup for {hostName}");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("deploy-keys/{hostName}")]

        [Authorize(Policy = Policies.BackupControl)]
        public async Task<IActionResult> DeployAllKeys(string hostName, [FromBody] DeployKeysRequest request)
        {
            try
            {
                if (!HostAllowed(hostName))
                    return Forbid();

                var success = await _backupService.DeployAllKeysAsync(hostName, request.HostPassword, request.StoragePassword);
                _audit.Log(User.Identity?.Name, "backup.keys.deploy", hostName, "api", success);
                if (success)
                {
                    return Ok(new { message = "All keys deployed successfully" });
                }
                return BadRequest(new { error = "Failed to deploy keys" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to deploy keys for {hostName}");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("reload-config")]

        [Authorize(Policy = Policies.BackupControl)]
        public IActionResult ReloadConfig()
        {
            _backupService.ReloadConfig();
            return Ok(new { message = "Config reloaded" });
        }
    }

    public class GenerateKeyRequest
    {
        public string KeyName { get; set; } = "";
    }
}