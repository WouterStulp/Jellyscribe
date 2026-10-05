using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LetterboxdSync.Api;

[ApiController]
[Route("LetterboxdSync/Web")]
public class SidebarController : ControllerBase
{
    private readonly Assembly _assembly = typeof(SidebarController).Assembly;

    [HttpGet("sidebar.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSidebarJs()
    {
        var stream = _assembly.GetManifestResourceStream("LetterboxdSync.Web.sidebar.js");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>Shared helpers for the admin and user pages, loaded by each page's inline script.</summary>
    [HttpGet("jellyscribe.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSharedJs()
    {
        var stream = _assembly.GetManifestResourceStream("LetterboxdSync.Web.jellyscribe.js");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>Shared styles for the admin and user pages, loaded by each page's inline script.</summary>
    [HttpGet("jellyscribe.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSharedCss()
    {
        var stream = _assembly.GetManifestResourceStream("LetterboxdSync.Web.jellyscribe.css");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "text/css");
    }
}
