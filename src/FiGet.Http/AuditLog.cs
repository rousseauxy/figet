using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FiGet.Http;

/// <summary>
/// Who changed what, and when. Separate from the request log on purpose: that one answers whether a client
/// reached this server, this one answers who changed something - a different question, a different
/// audience, and a different retention need.
///
/// Console only for now, under its own category so it can be filtered or shipped on its own. There is no
/// bespoke on/off setting: the category is <c>FiGet.Audit</c> and the standard log-level configuration
/// governs it, which means it is on wherever Information is on, and
/// <c>Logging:LogLevel:FiGet.Audit=None</c> silences it. On by default is deliberate - an audit trail that
/// has to be switched on in advance is not there on the day somebody asks what happened, and the volume is
/// a few lines a day rather than a few per request.
///
/// The database table and admin page this eventually wants are described in docs/backlog.md; a console
/// line is lost when the container's log rotates, which is precisely why it is not the finished article.
/// </summary>
public sealed class AuditLog(ILoggerFactory loggers)
{
    /// <summary>The logging category every audit line is written under.</summary>
    public const string Category = "FiGet.Audit";

    private readonly ILogger logger = loggers.CreateLogger(Category);

    /// <summary>
    /// Records one change. <paramref name="action"/> is a dotted verb such as <c>feed.create</c>, and
    /// <paramref name="subject"/> is what it acted on - a feed name, a token name, a package id.
    /// </summary>
    public void Record(HttpContext? http, string action, string subject, string? detail = null) =>
        logger.LogInformation(
            "{Action} {Subject} by {Actor} from {Caller}{Detail}",
            action,
            subject,
            RequestActor.Describe(http),
            RequestActor.Caller(http),
            string.IsNullOrEmpty(detail) ? "" : " | " + detail);
}
