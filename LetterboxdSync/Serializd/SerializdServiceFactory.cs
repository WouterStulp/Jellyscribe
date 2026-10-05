using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

public static class SerializdServiceFactory
{
    /// <summary>
    /// Test-only override. When non-null, <see cref="CreateAuthenticatedAsync"/>
    /// returns this instead of constructing a real client. Mirrors
    /// <see cref="LetterboxdServiceFactory.OverrideForTesting"/>. Production never sets it.
    /// </summary>
    internal static Func<string, string, ILogger, Task<ISerializdService>>? OverrideForTesting;

    /// <summary>
    /// Returns a logged-in client. When <paramref name="jellyfinUsername"/> is set (background sync
    /// paths), a rejected login also pushes an admin alert via <see cref="Notifier"/>.
    /// </summary>
    public static async Task<ISerializdService> CreateAuthenticatedAsync(string email, string password, ILogger logger,
        string? jellyfinUsername = null)
    {
        try
        {
            if (OverrideForTesting != null)
                return await OverrideForTesting(email, password, logger).ConfigureAwait(false);

            var client = new SerializdApiClient(logger);
            await client.AuthenticateAsync(email, password).ConfigureAwait(false);
            return client;
        }
        catch (SerializdAuthException ex) when (jellyfinUsername != null)
        {
            await Notifier.SerializdLoginFailedAsync(jellyfinUsername, email, ex.Message, logger).ConfigureAwait(false);
            throw;
        }
    }
}
