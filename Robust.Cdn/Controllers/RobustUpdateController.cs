using Microsoft.AspNetCore.Mvc;
using Quartz;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/robust")]
public sealed class RobustUpdateController(
    RobustAuthHelper authHelper,
    ISchedulerFactory schedulerFactory) : ControllerBase
{
    [HttpPost("control/update")]
    public async Task<IActionResult> PostControlUpdate()
    {
        if (!authHelper.IsAuthValid(out var failure))
            return failure;

        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(IngestNewCdnContentJob.Key, IngestNewCdnContentJob.Data(UpdateRobustManifestJob.ForkName));

        return Accepted();
    }
}

