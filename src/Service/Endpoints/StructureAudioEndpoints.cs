using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Doctheca.Service.Audio;

namespace Doctheca.Service;

/// <summary>Administrator-only recovery of unreferenced audio objects; no import or playback API.</summary>
public static class StructureAudioEndpoints
{
    public static WebApplication MapStructureAudioEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/structure-audio/orphan-sweep", async (StructureAudioService audio) =>
        {
            var result = await audio.SweepOrphansAsync();
            return result.Status == AudioResultStatus.Ok
                ? Results.Ok(new { success = true, data = result.Value })
                : Results.Json(new { success = false, message = result.Message, errorCode = result.ErrorCode },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
          .RequireServiceMantleSecurityResponseHeaders();
        return app;
    }
}
