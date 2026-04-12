using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Jobs;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

[Controller]
[Route("/robust")]
public sealed class RobustBuildPageController(
    ManifestDatabase database,
    IOptions<RobustOptions> robustOptions)
    : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        if (!TryCheckBasicAuth(out var errorResult)) return errorResult;

        var versions = new List<Version>();

        var dbVersions = database.Context.ForkVersions
            .AsNoTracking()
            .Where(v => v.Fork.Name == UpdateRobustManifestJob.ForkName && v.Available)
            .OrderByDescending(v => v.PublishedTime)
            .Take(50)
            .Select(v => new { v.Id, v.Name, v.PublishedTime, v.EngineVersion })
            .ToList();

        foreach (var dbVersion in dbVersions)
        {
            var servers = database.Context.ForkVersionServerBuilds
                .AsNoTracking()
                .Where(s => s.ForkVersionId == dbVersion.Id)
                .OrderBy(s => s.Platform)
                .Select(s => new VersionServer
                {
                    Platform = s.Platform,
                    FileName = s.FileName,
                    FileSize = s.FileSize
                })
                .ToArray();

            versions.Add(new Version
            {
                Name = dbVersion.Name,
                EngineVersion = dbVersion.EngineVersion,
                PublishedTime = DateTime.SpecifyKind(dbVersion.PublishedTime, DateTimeKind.Utc),
                Servers = servers
            });
        }

        return View("Index", new Model
        {
            Versions = versions
        });
    }

    private bool TryCheckBasicAuth(out IActionResult? errorResult)
    {
        var opts = robustOptions.Value;
        if (!opts.Private)
        {
            errorResult = null;
            return true;
        }

        return AuthorizationUtility.CheckBasicAuth(
            HttpContext,
            "robust",
            a => opts.PrivateUsers.GetValueOrDefault(a),
            out _,
            out errorResult);
    }

    public sealed class Model
    {
        public required List<Version> Versions;
    }

    public sealed class Version
    {
        public required string Name;
        public required DateTime PublishedTime;
        public required string? EngineVersion;
        public required VersionServer[] Servers;
    }

    public sealed class VersionServer
    {
        public required string Platform { get; set; }
        public required string FileName { get; set; }
        public required long? FileSize { get; set; }
    }

    private sealed class DbVersion
    {
        public required int Id { get; set; }
        public required string Name { get; set; }
        public required DateTime PublishedTime { get; set; }
        public required string? EngineVersion { get; set; }
    }
}

